using SystemAgent.Core.Contracts;
using SystemAgent.Core.Nodes;

namespace SystemAgent.Client;

// ノード・クラスタ・DB接続・冗長化・バックアップ
public sealed partial class ApiClient
{
    public Task<List<NodeInfo>> GetNodesAsync(CancellationToken cancellationToken = default) =>
        SendAsync<List<NodeInfo>>(HttpMethod.Get, "api/nodes", null, authorize: true, cancellationToken);

    public Task DeleteNodeAsync(Guid id, CancellationToken cancellationToken = default) =>
        SendAndDisposeAsync(HttpMethod.Delete, $"api/nodes/{id}", null, authorize: true, cancellationToken);

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
}
