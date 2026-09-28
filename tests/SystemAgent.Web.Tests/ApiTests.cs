using System.Net;
using System.Net.Http.Json;
using SystemAgent.Client;
using SystemAgent.Core.Contracts;

namespace SystemAgent.Web.Tests;

/// <summary>API の結合テスト（ルーティング・認可・例外の変換・認証の保護）。</summary>
public sealed class ApiTests : IDisposable
{
    private const string Password = "correct-horse-battery";
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task Health_IsAnonymous_AndReportsDatabaseUnavailableQuickly()
    {
        using var client = _app.CreateClient();
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var health = await client.GetFromJsonAsync<HealthResponse>("api/health", ApiClient.Json);

        Assert.Equal("ok", health!.Status);
        Assert.False(health.Database);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"{watch.Elapsed}");
    }

    [Theory]
    [InlineData("api/users")]
    [InlineData("api/nodes")]
    [InlineData("api/containers")]
    [InlineData("api/auth/me")]
    public async Task ProtectedApis_RequireAuthentication(string path)
    {
        using var client = _app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task Setup_RejectsWrongTokenAndWeakPassword_ThenCompletesOnce()
    {
        using var client = _app.CreateClient();
        Assert.True((await client.GetFromJsonAsync<SetupStatusResponse>("api/setup", ApiClient.Json))!.Required);

        var wrong = await client.PostAsJsonAsync("api/setup", new SetupRequest("wrong", "admin", Password), ApiClient.Json);
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);

        var containsUserName = await client.PostAsJsonAsync("api/setup", new SetupRequest(_app.SetupToken(), "admin", "admin-password-123"), ApiClient.Json);
        Assert.Equal(HttpStatusCode.BadRequest, containsUserName.StatusCode);

        var tooShort = await client.PostAsJsonAsync("api/setup", new SetupRequest(_app.SetupToken(), "admin", "short"), ApiClient.Json);
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);

        var token = _app.SetupToken();
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("api/setup", new SetupRequest(token, "admin", Password), ApiClient.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("api/setup", new SetupRequest(token, "admin", Password), ApiClient.Json)).StatusCode);
    }

    [Fact]
    public async Task EmergencyLogin_IssuesTokenUsableForApi()
    {
        await _app.SetupAsync();
        using var client = await _app.LoggedInClientAsync();

        var me = await client.GetFromJsonAsync<MeResponse>("api/auth/me", ApiClient.Json);

        Assert.Equal(("admin", AuthSources.Emergency), (me!.UserName, me.AuthSource));
    }

    [Fact]
    public async Task EmergencyLogin_WrongPassword_Is401()
    {
        await _app.SetupAsync();
        using var client = _app.CreateClient();
        var response = await client.PostAsJsonAsync("api/auth/emergency-login", new LoginRequest("admin", "wrong-password-1"), ApiClient.Json);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task EmergencyLogin_LocksAccountAfterRepeatedFailures()
    {
        await _app.SetupAsync();
        using var client = _app.CreateClient();
        for (var i = 0; i < 5; i++)
            await client.PostAsJsonAsync("api/auth/emergency-login", new LoginRequest("admin", "wrong-password-1"), ApiClient.Json);

        // 正しいパスワードでもロックアウト中は受け付けない
        var locked = await client.PostAsJsonAsync("api/auth/emergency-login", new LoginRequest("admin", Password), ApiClient.Json);

        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.True(locked.Headers.RetryAfter is not null);
    }

    [Fact]
    public async Task ChangeEmergencyPassword_RequiresCurrentPassword()
    {
        await _app.SetupAsync();
        using var client = await _app.LoggedInClientAsync();
        const string path = "api/auth/emergency-users/admin/password";

        var wrongCurrent = await client.PutAsJsonAsync(path, new ChangePasswordRequest("wrong-password-1", "new-password-4567"), ApiClient.Json);
        Assert.Equal(HttpStatusCode.BadRequest, wrongCurrent.StatusCode);

        var same = await client.PutAsJsonAsync(path, new ChangePasswordRequest(Password, Password), ApiClient.Json);
        Assert.Equal(HttpStatusCode.BadRequest, same.StatusCode);

        var changed = await client.PutAsJsonAsync(path, new ChangePasswordRequest(Password, "new-password-4567"), ApiClient.Json);
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);

        using var _ = await _app.LoggedInClientAsync("admin", "new-password-4567");
    }

    [Fact]
    public async Task DatabaseApis_Return503WhenDatabaseIsNotConfigured()
    {
        await _app.SetupAsync();
        using var client = await _app.LoggedInClientAsync();

        var response = await client.GetAsync("api/users");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("DB接続が設定されていません", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("api/Nodes/00000000-0000-0000-0000-000000000001/proxy/api/containers")]
    [InlineData("api/NODES/00000000-0000-0000-0000-000000000001/proxy/api/containers")]
    [InlineData("api/containers/../nodes/00000000-0000-0000-0000-000000000001/proxy/api/x")]
    [InlineData("other")]
    public async Task Proxy_RejectsNonForwardablePaths(string target)
    {
        await _app.SetupAsync();
        using var client = await _app.LoggedInClientAsync();

        var response = await client.GetAsync($"api/nodes/{Guid.NewGuid()}/proxy/{target.Replace("..", "%2E%2E")}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}

/// <summary>接続元ごとのレート制限（少ない上限で確かめる）。</summary>
public sealed class RateLimitTests : IDisposable
{
    private readonly TestApp _app = new(new() { ["Auth:RateLimitPerMinute"] = "3" });

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task LoginAttemptsBeyondLimit_Are429()
    {
        using var client = _app.CreateClient();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            var response = await client.PostAsJsonAsync("api/auth/emergency-login", new LoginRequest($"user{i}", "wrong-password-1"), ApiClient.Json);
            statuses.Add(response.StatusCode);
        }

        Assert.Equal([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests], statuses);
    }
}
