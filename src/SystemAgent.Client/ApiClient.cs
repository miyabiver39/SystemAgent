using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SystemAgent.Core.Contracts;
using SystemAgent.Core.Nodes;
using SystemAgent.Core.Users;

namespace SystemAgent.Client;

/// <summary>アクセストークンの保管先。WebUIはブラウザのsessionStorage、CLIはユーザーのホームディレクトリ。</summary>
public interface ITokenProvider
{
    ValueTask<string?> GetAccessTokenAsync();

    /// <summary>APIが401を返した（トークン失効）ときに呼ばれる。</summary>
    ValueTask OnUnauthorizedAsync();
}

/// <summary>
/// WebAPIのクライアント。WebUIとCLIはこのクラスだけを通してAPIを使い、業務ロジックを持たない（基本設計書 9章、ADR-007）。
/// 両者の操作が同一であることはこの共有によって担保する。
/// </summary>
public sealed class ApiClient(HttpClient http, ITokenProvider tokens)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public Task<HealthResponse> GetHealthAsync(CancellationToken cancellationToken = default) =>
        SendAsync<HealthResponse>(HttpMethod.Get, "api/health", null, authorize: false, cancellationToken);

    public Task<SetupStatusResponse> GetSetupStatusAsync(CancellationToken cancellationToken = default) =>
        SendAsync<SetupStatusResponse>(HttpMethod.Get, "api/setup", null, authorize: false, cancellationToken);

    public Task CompleteSetupAsync(SetupRequest request, CancellationToken cancellationToken = default) =>
        SendAndDisposeAsync(HttpMethod.Post, "api/setup", request, authorize: false, cancellationToken);

    public Task<TokenResponse> LoginAsync(string userName, string password, bool emergency, CancellationToken cancellationToken = default) =>
        SendAsync<TokenResponse>(HttpMethod.Post, emergency ? "api/auth/emergency-login" : "api/auth/login",
            new LoginRequest(userName, password), authorize: false, cancellationToken);

    public Task<MeResponse> GetMeAsync(CancellationToken cancellationToken = default) =>
        SendAsync<MeResponse>(HttpMethod.Get, "api/auth/me", null, authorize: true, cancellationToken);

    public Task<List<NodeInfo>> GetNodesAsync(CancellationToken cancellationToken = default) =>
        SendAsync<List<NodeInfo>>(HttpMethod.Get, "api/nodes", null, authorize: true, cancellationToken);

    public Task<NodeInfo> RegisterNodeAsync(RegisterNodeRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<NodeInfo>(HttpMethod.Post, "api/nodes", request, authorize: true, cancellationToken);

    public Task DeleteNodeAsync(Guid id, CancellationToken cancellationToken = default) =>
        SendAndDisposeAsync(HttpMethod.Delete, $"api/nodes/{id}", null, authorize: true, cancellationToken);

    public Task<List<UserSummary>> GetUsersAsync(CancellationToken cancellationToken = default) =>
        SendAsync<List<UserSummary>>(HttpMethod.Get, "api/users", null, authorize: true, cancellationToken);

    public Task<UserSummary> CreateUserAsync(CreateUserRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<UserSummary>(HttpMethod.Post, "api/users", request, authorize: true, cancellationToken);

    public Task DeleteUserAsync(string userName, CancellationToken cancellationToken = default) =>
        SendAndDisposeAsync(HttpMethod.Delete, $"api/users/{Uri.EscapeDataString(userName)}", null, authorize: true, cancellationToken);

    public Task ChangePasswordAsync(string userName, string newPassword, bool emergency, CancellationToken cancellationToken = default)
    {
        var path = emergency
            ? $"api/auth/emergency-users/{Uri.EscapeDataString(userName)}/password"
            : $"api/users/{Uri.EscapeDataString(userName)}/password";
        return SendAndDisposeAsync(HttpMethod.Put, path, new ChangePasswordRequest(newPassword), authorize: true, cancellationToken);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, bool authorize, CancellationToken cancellationToken)
    {
        using var response = await SendCoreAsync(method, path, body, authorize, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken))!;
    }

    private async Task SendAndDisposeAsync(HttpMethod method, string path, object? body, bool authorize, CancellationToken cancellationToken)
    {
        using var _ = await SendCoreAsync(method, path, body, authorize, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendCoreAsync(
        HttpMethod method, string path, object? body, bool authorize, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        if (authorize && await tokens.GetAccessTokenAsync() is { } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var response = await http.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode) return response;

        using (response)
        {
            if (authorize && response.StatusCode == HttpStatusCode.Unauthorized)
            {
                await tokens.OnUnauthorizedAsync();
            }
            throw new ApiException(response.StatusCode, await ReadProblemAsync(response, cancellationToken));
        }
    }

    private static async Task<string?> ReadProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<Problem>(Json, cancellationToken);
            if (problem?.Errors is { Count: > 0 } errors) return string.Join(" ", errors.SelectMany(e => e.Value));
            return problem?.Detail ?? problem?.Title;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record Problem(string? Title, string? Detail, Dictionary<string, string[]>? Errors);
}

public sealed class ApiException(HttpStatusCode statusCode, string? detail)
    : Exception(detail ?? $"APIエラー ({(int)statusCode})")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}
