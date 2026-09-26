using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using SystemAgent.Core.Nodes;
using SystemAgent.Core.Users;
using SystemAgent.Web.Auth;
using SystemAgent.Web.Controllers;

namespace SystemAgent.Web.Client;

/// <summary>
/// WebUIからWebAPIを呼び出すクライアント。WebUIは業務ロジックを持たず、必ずこのクライアント経由でAPIを使う（基本設計書 9章、ADR-007）。
/// </summary>
public sealed class ApiClient(HttpClient http, TokenStore tokens)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public Task<HealthResponse> GetHealthAsync(CancellationToken cancellationToken = default) =>
        SendAsync<HealthResponse>(HttpMethod.Get, "api/health", null, authorize: false, cancellationToken);

    public Task<TokenResponse> LoginAsync(string userName, string password, bool emergency, CancellationToken cancellationToken = default) =>
        SendAsync<TokenResponse>(HttpMethod.Post, emergency ? "api/auth/emergency-login" : "api/auth/login",
            new LoginRequest(userName, password), authorize: false, cancellationToken);

    public Task<List<NodeInfo>> GetNodesAsync(CancellationToken cancellationToken = default) =>
        SendAsync<List<NodeInfo>>(HttpMethod.Get, "api/nodes", null, authorize: true, cancellationToken);

    public Task<NodeInfo> RegisterNodeAsync(RegisterNodeRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<NodeInfo>(HttpMethod.Post, "api/nodes", request, authorize: true, cancellationToken);

    public Task DeleteNodeAsync(Guid id, CancellationToken cancellationToken = default) =>
        SendCoreAsync(HttpMethod.Delete, $"api/nodes/{id}", null, authorize: true, cancellationToken);

    public Task<List<UserSummary>> GetUsersAsync(CancellationToken cancellationToken = default) =>
        SendAsync<List<UserSummary>>(HttpMethod.Get, "api/users", null, authorize: true, cancellationToken);

    public Task<UserSummary> CreateUserAsync(CreateUserRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<UserSummary>(HttpMethod.Post, "api/users", request, authorize: true, cancellationToken);

    public Task DeleteUserAsync(string userName, CancellationToken cancellationToken = default) =>
        SendCoreAsync(HttpMethod.Delete, $"api/users/{Uri.EscapeDataString(userName)}", null, authorize: true, cancellationToken);

    public Task ChangePasswordAsync(string userName, string newPassword, bool emergency, CancellationToken cancellationToken = default)
    {
        var path = emergency
            ? $"api/auth/emergency-users/{Uri.EscapeDataString(userName)}/password"
            : $"api/users/{Uri.EscapeDataString(userName)}/password";
        return SendCoreAsync(HttpMethod.Put, path, new ChangePasswordRequest(newPassword), authorize: true, cancellationToken);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, bool authorize, CancellationToken cancellationToken)
    {
        using var response = await SendCoreAsync(method, path, body, authorize, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken))!;
    }

    private async Task<HttpResponseMessage> SendCoreAsync(
        HttpMethod method, string path, object? body, bool authorize, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        if (authorize && await tokens.GetAsync() is { } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        }

        var response = await http.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode) return response;

        using (response)
        {
            if (authorize && response.StatusCode == HttpStatusCode.Unauthorized)
            {
                // トークン失効。クリアするとレイアウトがログイン画面へ遷移させる。
                await tokens.ClearAsync();
            }
            throw new ApiException(response.StatusCode, await ReadProblemAsync(response, cancellationToken));
        }
    }

    private static async Task<string?> ReadProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(Json, cancellationToken);
            if (problem?.Errors is { Count: > 0 } errors) return string.Join(" ", errors.SelectMany(e => e.Value));
            return problem?.Detail ?? problem?.Title;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed class ApiException(HttpStatusCode statusCode, string? detail)
    : Exception(detail ?? $"APIエラー ({(int)statusCode})")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}
