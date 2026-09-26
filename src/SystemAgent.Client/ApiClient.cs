using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SystemAgent.Core.CapabilityProviders;
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
    // ForNodeで作ったクライアントは、api/... を api/nodes/{id}/proxy/api/... に読み替えて他ノードに転送させる
    private string? _nodePrefix;

    /// <summary>指定ノード（nullならこのノード）を操作するクライアント。ログイン等の認証APIはこのノードで行う。</summary>
    public ApiClient ForNode(Guid? nodeId) =>
        nodeId is null ? this : new ApiClient(http, tokens) { _nodePrefix = $"api/nodes/{nodeId}/proxy/" };

    /// <summary>イメージのpull/取り込み（テンプレート上の上限30分）を待てるHttpClientのタイムアウト。</summary>
    public static readonly TimeSpan HttpTimeout = TimeSpan.FromMinutes(35);

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

    public Task DeleteNodeAsync(Guid id, CancellationToken cancellationToken = default) =>
        SendAndDisposeAsync(HttpMethod.Delete, $"api/nodes/{id}", null, authorize: true, cancellationToken);

    public Task<List<UserSummary>> GetUsersAsync(CancellationToken cancellationToken = default) =>
        SendAsync<List<UserSummary>>(HttpMethod.Get, "api/users", null, authorize: true, cancellationToken);

    public Task<UserSummary> CreateUserAsync(CreateUserRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<UserSummary>(HttpMethod.Post, "api/users", request, authorize: true, cancellationToken);

    public Task DeleteUserAsync(string userName, CancellationToken cancellationToken = default) =>
        SendAndDisposeAsync(HttpMethod.Delete, $"api/users/{Uri.EscapeDataString(userName)}", null, authorize: true, cancellationToken);

    public Task<ClusterStatusResponse> GetClusterAsync(CancellationToken cancellationToken = default) =>
        SendAsync<ClusterStatusResponse>(HttpMethod.Get, "api/cluster", null, authorize: true, cancellationToken);

    public Task<ClusterStatusResponse> InitializeClusterAsync(string clusterName, CancellationToken cancellationToken = default) =>
        SendAsync<ClusterStatusResponse>(HttpMethod.Post, "api/cluster/init", new InitializeClusterRequest(clusterName), authorize: true, cancellationToken);

    public Task<JoinTokenResponse> CreateJoinTokenAsync(int validMinutes, CancellationToken cancellationToken = default) =>
        SendAsync<JoinTokenResponse>(HttpMethod.Post, "api/cluster/tokens", new CreateJoinTokenRequest(validMinutes), authorize: true, cancellationToken);

    public Task<ClusterStatusResponse> JoinClusterAsync(string token, CancellationToken cancellationToken = default) =>
        SendAsync<ClusterStatusResponse>(HttpMethod.Post, "api/cluster/join", new JoinClusterRequest(token), authorize: true, cancellationToken);

    public Task<DatabaseSettingsResponse> GetDatabaseAsync(CancellationToken cancellationToken = default) =>
        SendAsync<DatabaseSettingsResponse>(HttpMethod.Get, "api/system/database", null, authorize: true, cancellationToken);

    public Task SetDatabaseAsync(SetDatabaseRequest request, CancellationToken cancellationToken = default) =>
        SendAndDisposeAsync(HttpMethod.Put, "api/system/database", request, authorize: true, cancellationToken);

    public Task MigrateDatabaseAsync(CancellationToken cancellationToken = default) =>
        SendAndDisposeAsync(HttpMethod.Post, "api/system/database/migrate", null, authorize: true, cancellationToken);

    public Task<HaStatusResponse> GetHaAsync(CancellationToken cancellationToken = default) =>
        SendAsync<HaStatusResponse>(HttpMethod.Get, "api/ha", null, authorize: true, cancellationToken);

    public Task SaveHaAsync(SetHaSettingsRequest request, CancellationToken cancellationToken = default) =>
        SendAndDisposeAsync(HttpMethod.Put, "api/ha", request, authorize: true, cancellationToken);

    public Task ApplyHaAsync(CancellationToken cancellationToken = default) =>
        SendAndDisposeAsync(HttpMethod.Post, "api/ha/apply", null, authorize: true, cancellationToken);

    public Task RejoinHaAsync(CancellationToken cancellationToken = default) =>
        SendAndDisposeAsync(HttpMethod.Post, "api/ha/rejoin", null, authorize: true, cancellationToken);

    public Task<List<ManagedServiceResponse>> GetServicesAsync(CancellationToken cancellationToken = default) =>
        SendAsync<List<ManagedServiceResponse>>(HttpMethod.Get, "api/services", null, authorize: true, cancellationToken);

    public Task<ManagedServiceResponse> GetServiceAsync(string unit, CancellationToken cancellationToken = default) =>
        SendAsync<ManagedServiceResponse>(HttpMethod.Get, $"api/services/{Uri.EscapeDataString(unit)}", null, authorize: true, cancellationToken);

    public Task ServiceActionAsync(string unit, ServiceAction action, CancellationToken cancellationToken = default) =>
        SendAndDisposeAsync(HttpMethod.Post, $"api/services/{Uri.EscapeDataString(unit)}/{action}", null, authorize: true, cancellationToken);

    public Task<ServiceLogsResponse> GetServiceLogsAsync(string unit, int lines, CancellationToken cancellationToken = default) =>
        SendAsync<ServiceLogsResponse>(HttpMethod.Get, $"api/services/{Uri.EscapeDataString(unit)}/logs?lines={lines}", null, authorize: true, cancellationToken);

    public Task<BackupListResponse> GetBackupsAsync(CancellationToken cancellationToken = default) =>
        SendAsync<BackupListResponse>(HttpMethod.Get, "api/backups", null, authorize: true, cancellationToken);

    public Task<BackupFileInfo> CreateBackupAsync(CancellationToken cancellationToken = default) =>
        SendAsync<BackupFileInfo>(HttpMethod.Post, "api/backups", null, authorize: true, cancellationToken);

    public Task DeleteBackupAsync(string name, CancellationToken cancellationToken = default) =>
        SendAndDisposeAsync(HttpMethod.Delete, $"api/backups/{Uri.EscapeDataString(name)}", null, authorize: true, cancellationToken);

    public Task RestoreBackupAsync(string name, CancellationToken cancellationToken = default) =>
        SendAndDisposeAsync(HttpMethod.Post, $"api/backups/{Uri.EscapeDataString(name)}/restore", new RestoreBackupRequest(name), authorize: true, cancellationToken);

    /// <summary>バックアップファイルをストリームで取得する（呼び出し側で破棄する）。</summary>
    public async Task<Stream> DownloadBackupAsync(string name, CancellationToken cancellationToken = default)
    {
        var response = await SendCoreAsync(HttpMethod.Get, $"api/backups/{Uri.EscapeDataString(name)}", null, authorize: true, cancellationToken,
            HttpCompletionOption.ResponseHeadersRead);
        return new ResponseStream(response, await response.Content.ReadAsStreamAsync(cancellationToken));
    }

    public Task<HostEnvironment> GetEnvironmentAsync(bool refresh = false, CancellationToken cancellationToken = default) =>
        SendAsync<HostEnvironment>(HttpMethod.Get, $"api/system/environment?refresh={refresh}", null, authorize: true, cancellationToken);

    public Task<ContainerRuntimeResponse> GetContainerRuntimeAsync(CancellationToken cancellationToken = default) =>
        SendAsync<ContainerRuntimeResponse>(HttpMethod.Get, "api/containers/runtime", null, authorize: true, cancellationToken);

    public Task<List<ContainerInfo>> GetContainersAsync(CancellationToken cancellationToken = default) =>
        SendAsync<List<ContainerInfo>>(HttpMethod.Get, "api/containers", null, authorize: true, cancellationToken);

    /// <param name="action">start / stop / restart</param>
    public Task ContainerActionAsync(string id, string action, CancellationToken cancellationToken = default) =>
        SendAndDisposeAsync(HttpMethod.Post, $"api/containers/{Uri.EscapeDataString(id)}/{action}", null, authorize: true, cancellationToken);

    public Task RemoveContainerAsync(string id, CancellationToken cancellationToken = default) =>
        SendAndDisposeAsync(HttpMethod.Delete, $"api/containers/{Uri.EscapeDataString(id)}", null, authorize: true, cancellationToken);

    public Task<ContainerLogsResponse> GetContainerLogsAsync(string id, int tail, CancellationToken cancellationToken = default) =>
        SendAsync<ContainerLogsResponse>(HttpMethod.Get, $"api/containers/{Uri.EscapeDataString(id)}/logs?tail={tail}", null, authorize: true, cancellationToken);

    public Task<List<PodInfo>> GetPodsAsync(CancellationToken cancellationToken = default) =>
        SendAsync<List<PodInfo>>(HttpMethod.Get, "api/pods", null, authorize: true, cancellationToken);

    public Task<List<ImageInfo>> GetImagesAsync(CancellationToken cancellationToken = default) =>
        SendAsync<List<ImageInfo>>(HttpMethod.Get, "api/images", null, authorize: true, cancellationToken);

    public Task PullImageAsync(string image, CancellationToken cancellationToken = default) =>
        SendAndDisposeAsync(HttpMethod.Post, "api/images/pull", new PullImageRequest(image), authorize: true, cancellationToken);

    public Task RemoveImageAsync(string id, CancellationToken cancellationToken = default) =>
        SendAndDisposeAsync(HttpMethod.Delete, $"api/images/{Uri.EscapeDataString(id)}", null, authorize: true, cancellationToken);

    /// <summary>イメージアーカイブ(tar)をストリームのまま送信する。</summary>
    public async Task<ImportImageResponse> ImportImageAsync(Stream archive, string fileName, CancellationToken cancellationToken = default)
    {
        using var content = new MultipartFormDataContent { { new StreamContent(archive), "file", fileName } };
        using var response = await SendCoreAsync(HttpMethod.Post, "api/images/import", content, authorize: true, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<ImportImageResponse>(Json, cancellationToken))!;
    }

    public Task<NetworkStatus> GetNetworkAsync(CancellationToken cancellationToken = default) =>
        SendAsync<NetworkStatus>(HttpMethod.Get, "api/network", null, authorize: true, cancellationToken);

    public Task<NtpResponse> GetNtpAsync(CancellationToken cancellationToken = default) =>
        SendAsync<NtpResponse>(HttpMethod.Get, "api/ntp", null, authorize: true, cancellationToken);

    public Task SetNtpServersAsync(IReadOnlyList<string> servers, CancellationToken cancellationToken = default) =>
        SendAndDisposeAsync(HttpMethod.Put, "api/ntp/servers", new SetNtpServersRequest(servers), authorize: true, cancellationToken);

    public Task SyncNtpAsync(CancellationToken cancellationToken = default) =>
        SendAndDisposeAsync(HttpMethod.Post, "api/ntp/sync", null, authorize: true, cancellationToken);

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
        HttpMethod method, string path, object? body, bool authorize, CancellationToken cancellationToken,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        if (_nodePrefix is not null && path.StartsWith("api/", StringComparison.Ordinal))
        {
            path = _nodePrefix + path;
            // 他ノードへの転送APIは認証が必要（転送先で認証不要なAPIでも）
            authorize = true;
        }
        using var request = new HttpRequestMessage(method, path);
        request.Content = body switch
        {
            null => null,
            HttpContent content => content,
            _ => JsonContent.Create(body, options: Json),
        };
        if (authorize && await tokens.GetAccessTokenAsync() is { } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var response = await http.SendAsync(request, completion, cancellationToken);
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

/// <summary>応答本文のストリーム。破棄時に応答も破棄する。</summary>
internal sealed class ResponseStream(HttpResponseMessage response, Stream inner) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
            response.Dispose();
        }
        base.Dispose(disposing);
    }
}
