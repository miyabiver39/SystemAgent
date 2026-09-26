using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.FileProviders;
using SystemAgent.Client;
using SystemAgent.Core.Security;
using SystemAgent.Infrastructure;
using SystemAgent.Web.Api;
using SystemAgent.Web.Auth;
using SystemAgent.Web.Client;
using SystemAgent.Web.Components;

// パッケージ導入時（/usr/lib/systemagent）は起動時のカレントディレクトリが任意のため、配置先をコンテンツルートにする。
// 開発時（dotnet run）はプロジェクトディレクトリに appsettings.json があるのでそのまま
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = File.Exists("appsettings.json") ? null : AppContext.BaseDirectory,
});

// RPM/DEBで導入した場合の設定ファイル（ポート・ノード名等。DB接続情報はここではなく暗号化して保存する）。
// appsettings*.json の後、環境変数より前に読む（環境変数で上書きできるように）
if (!OperatingSystem.IsWindows())
{
    var lastJson = builder.Configuration.Sources.ToList().FindLastIndex(s => s is JsonConfigurationSource);
    builder.Configuration.Sources.Insert(lastJson + 1, new JsonConfigurationSource
    {
        Path = "etc/systemagent/systemagent.json",
        Optional = true,
        FileProvider = new PhysicalFileProvider("/"),
    });
}

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
// keepalived の notify から呼ばれる CLI が使うフック用トークン（root専用ファイル）
app.Services.GetRequiredService<SystemAgent.Infrastructure.Ha.HaService>().EnsureHookToken();

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
