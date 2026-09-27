using SystemAgent.Core.Contracts;
using SystemAgent.Core.Users;

namespace SystemAgent.Client;

// 認証・初期セットアップ・ユーザー
public sealed partial class ApiClient
{
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
}
