using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using SystemAgent.Client;
using SystemAgent.Core.Security;
using SystemAgent.Infrastructure;
using SystemAgent.Web.Api;
using SystemAgent.Web.Auth;
using SystemAgent.Web.Client;
using SystemAgent.Web.Components;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel((context, kestrel) => KestrelEndpoints.Configure(kestrel, context.Configuration));

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// WebUIとWebAPIは同一プロセス・同一ポートでホストする（ADR-007）。
// CLIおよびBlazorコンポーネントはいずれもこのWebAPIをクライアントとして呼び出す。
builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi();

builder.Services.AddInfrastructure(builder.Configuration, builder.Environment.ContentRootPath);

builder.Services.AddSingleton<JwtTokenIssuer>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer()
    .AddScheme<AuthenticationSchemeOptions, NodeCertificateAuthenticationHandler>(NodeCertificateAuthenticationHandler.SchemeName, null);
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<ILocalSecretStore>((options, secrets) =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = JwtTokenIssuer.CreateValidationParameters(secrets);
    });
// 利用者（JWT）と、他ノードから転送された操作（ノード証明書 + 操作者ヘッダ）のどちらでも認可する
builder.Services.AddAuthorization(options => options.DefaultPolicy = new AuthorizationPolicyBuilder(
        JwtBearerDefaults.AuthenticationScheme, NodeCertificateAuthenticationHandler.SchemeName)
    .RequireAuthenticatedUser()
    .Build());

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();

// WebUI: 画面文字列はリソースファイルに外出しする（ADR-011）
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<TokenStore>();
builder.Services.AddScoped<NodeSelection>();
builder.Services.AddScoped<ITokenProvider>(sp => sp.GetRequiredService<TokenStore>());
builder.Services.AddScoped<ApiAuthenticationStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<ApiAuthenticationStateProvider>());
builder.Services.AddSingleton<ApiBaseAddress>();
builder.Services.AddHttpClient<ApiClient>((sp, client) =>
{
    client.BaseAddress = sp.GetRequiredService<ApiBaseAddress>().Value;
    client.Timeout = ApiClient.HttpTimeout;
});

var app = builder.Build();

// 秘密情報ストアを起動時に初期化し、初回起動時の初期パスワード案内をログに出す
app.Services.GetRequiredService<ILocalSecretStore>();

app.UseRequestLocalization("ja-JP");

// Configure the HTTP request pipeline.
app.UseExceptionHandler("/Error", createScopeForErrors: true);
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}
else
{
    app.MapOpenApi();
}

// APIのステータスコード(401/404等)はそのまま返し、画面遷移のみNotFoundページへ再実行する
app.UseWhen(
    context => !context.Request.Path.StartsWithSegments("/api"),
    branch => branch.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));

// HTTPSリダイレクトは行わない。TLS終端はNginx(任意)に委ね、HTTP平文も許可する（ADR-004）。

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapControllers();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
