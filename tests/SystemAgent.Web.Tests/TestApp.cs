using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using SystemAgent.Client;
using SystemAgent.Core.Contracts;

namespace SystemAgent.Web.Tests;

/// <summary>
/// テスト用に起動する SystemAgent.Web。秘密情報は一時ディレクトリに置き、DB接続は未設定（DBを使うAPIは503になる）。
/// </summary>
public sealed class TestApp : WebApplicationFactory<Program>
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sa-web-tests-" + Guid.NewGuid());
    private readonly Dictionary<string, string?> _settings;

    public TestApp(Dictionary<string, string?>? settings = null) => _settings = settings ?? [];

    public string SecretsDir => Path.Combine(_dir, "secrets");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // 開発用の設定（WSLのDB接続）を読まない
        builder.UseEnvironment("Testing");
        builder.UseSetting("SecretStore:Path", SecretsDir);
        builder.UseSetting("ConnectionStrings:Default", "");
        builder.UseSetting("Cluster:Port", "0");
        builder.UseSetting("Cluster:NodeName", "test-node");
        foreach (var (key, value) in _settings) builder.UseSetting(key, value);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    public string SetupToken() => File.ReadAllText(Path.Combine(SecretsDir, "setup-token")).Trim();

    /// <summary>初期セットアップで緊急認証の管理者を作る。</summary>
    public async Task SetupAsync(string userName = "admin", string password = "correct-horse-battery")
    {
        using var client = CreateClient();
        var response = await client.PostAsJsonAsync("api/setup", new SetupRequest(SetupToken(), userName, password), ApiClient.Json);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>緊急ログインしたクライアント。</summary>
    public async Task<HttpClient> LoggedInClientAsync(string userName = "admin", string password = "correct-horse-battery")
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("api/auth/emergency-login", new LoginRequest(userName, password), ApiClient.Json);
        response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadFromJsonAsync<TokenResponse>(ApiClient.Json))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return client;
    }
}
