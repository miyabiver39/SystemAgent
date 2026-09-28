using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SystemAgent.Client;
using SystemAgent.Core.Contracts;
using SystemAgent.Core.Nodes;

// 標準出力を差し替えてコマンドの出力を確かめるため、テストは順に実行する
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace SystemAgent.Cli.Tests;

/// <summary>
/// CLI を実際の引数で実行し、APIの呼び方（パス・クエリ）と表示・終了コードを確かめる。
/// 接続先はテスト内で起動する疑似サーバー（本物のHTTP）。
/// </summary>
public sealed class CliTests : IAsyncLifetime
{
    private static readonly Guid NodeA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sa-cli-tests-" + Guid.NewGuid());
    private readonly List<string> _requests = [];
    private WebApplication _server = default!;
    private string _url = "";

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _server = builder.Build();
        _server.Use(async (context, next) =>
        {
            lock (_requests) _requests.Add($"{context.Request.Method} {context.Request.Path}{context.Request.QueryString}");
            await next(context);
        });
        _server.MapGet("/api/health", () => Results.Json(new HealthResponse("ok", false, DateTimeOffset.UtcNow), ApiClient.Json));
        _server.MapGet("/api/setup", () => Results.Json(new SetupStatusResponse(false), ApiClient.Json));
        _server.MapGet("/api/nodes", () => Results.Json(new List<NodeInfo>
        {
            new(NodeA, "node-a", "10.0.0.11", 5443, new OsInfo("almalinux", "9.8", "x86_64"), NodeRole.Managed, DateTimeOffset.UtcNow, null),
        }, ApiClient.Json));
        _server.MapDelete("/api/nodes/{id:guid}", (Guid id, bool? force) => force == true
            ? Results.NoContent()
            : Results.Problem(statusCode: 409, detail: "操作中のこのノード自身の登録は削除できません。"));
        _server.MapGet("/api/users", () => Results.Problem(statusCode: 503, detail: "DB接続が設定されていません。"));
        await _server.StartAsync();
        _url = _server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        Directory.Delete(_dir, recursive: true);
    }

    private FileTokenStore Tokens() => new(Path.Combine(_dir, "session.json"));

    private async Task<(int ExitCode, string Output, string Error)> RunAsync(params string[] args)
    {
        var (originalOut, originalError) = (Console.Out, Console.Error);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Console.SetOut(output);
        Console.SetError(error);
        try
        {
            var root = CliApp.CreateRootCommand(new CliContext(Tokens()));
            var exitCode = await root.Parse([.. args, "--url", _url]).InvokeAsync();
            return (exitCode, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    [Fact]
    public async Task Status_Json_ReportsHealthAndLoginState()
    {
        var (exitCode, output, _) = await RunAsync("status", "--json");

        Assert.Equal(0, exitCode);
        using var json = JsonDocument.Parse(output);
        Assert.False(json.RootElement.GetProperty("health").GetProperty("database").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("me").ValueKind);
        Assert.Contains("GET /api/health", _requests);
    }

    [Fact]
    public async Task NodeList_Json_ReturnsNodes()
    {
        var (exitCode, output, _) = await RunAsync("node", "list", "--json");

        Assert.Equal(0, exitCode);
        var nodes = JsonSerializer.Deserialize<List<NodeInfo>>(output, ApiClient.Json)!;
        Assert.Equal("node-a", Assert.Single(nodes).HostName);
    }

    [Fact]
    public async Task NodeDelete_WithoutForce_ShowsServerReasonAndFails()
    {
        var (exitCode, _, error) = await RunAsync("node", "delete", NodeA.ToString());

        Assert.Equal(1, exitCode);
        Assert.Contains("エラー: 操作中のこのノード自身の登録は削除できません。", error);
        Assert.Contains($"DELETE /api/nodes/{NodeA}", _requests);
    }

    [Fact]
    public async Task NodeDelete_WithForce_PassesQuery()
    {
        var (exitCode, output, _) = await RunAsync("node", "delete", NodeA.ToString(), "--force");

        Assert.Equal(0, exitCode);
        Assert.Contains("ノードを削除しました。", output);
        Assert.Contains($"DELETE /api/nodes/{NodeA}?force=true", _requests);
    }

    [Fact]
    public async Task ServiceUnavailable_IsReportedAsError()
    {
        var (exitCode, _, error) = await RunAsync("user", "list");

        Assert.Equal(1, exitCode);
        Assert.Contains("エラー: DB接続が設定されていません。", error);
    }

    [Fact]
    public async Task InvalidArguments_AreRejectedWithoutCallingServer()
    {
        var (exitCode, _, _) = await RunAsync("node", "delete", "not-a-guid");

        Assert.NotEqual(0, exitCode);
        Assert.DoesNotContain(_requests, r => r.StartsWith("DELETE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnreachableServer_IsReportedAsConnectionError()
    {
        var url = _url;
        _url = "http://127.0.0.1:1";
        try
        {
            var (exitCode, _, error) = await RunAsync("status");
            Assert.Equal(1, exitCode);
            Assert.Contains("に接続できません", error);
        }
        finally
        {
            _url = url;
        }
    }
}

public sealed class FileTokenStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sa-token-tests-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string SessionPath => Path.Combine(_dir, "systemagent", "session.json");

    [Fact]
    public void SaveAndLoad_RoundTrip_AndFileDoesNotContainTokenInPlainTextOnWindows()
    {
        var store = new FileTokenStore(SessionPath);
        var session = new Session("http://localhost:5000", "admin",
            new TokenResponse("secret-access-token", DateTimeOffset.UtcNow.AddHours(1), AuthSources.Emergency));

        store.Save(session);

        var loaded = new FileTokenStore(SessionPath).Load()!;
        Assert.Equal(("admin", "secret-access-token"), (loaded.UserName, loaded.Token.AccessToken));
        var content = File.ReadAllText(SessionPath);
        if (OperatingSystem.IsWindows())
        {
            Assert.StartsWith("DPAPI:", content);
            Assert.DoesNotContain("secret-access-token", content);
        }
        else
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(SessionPath));
        }
    }

    [Fact]
    public void ExpiredSession_IsNotLoaded()
    {
        var store = new FileTokenStore(SessionPath);
        store.Save(new Session("http://localhost:5000", "admin",
            new TokenResponse("t", DateTimeOffset.UtcNow.AddMinutes(-1), AuthSources.Database)));

        Assert.Null(store.Load());
    }

    [Fact]
    public void UnreadableProtectedFile_IsTreatedAsLoggedOut()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SessionPath)!);
        File.WriteAllText(SessionPath, "DPAPI:not-base64!!");

        Assert.Null(new FileTokenStore(SessionPath).Load());
    }
}
