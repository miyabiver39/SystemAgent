using System.Collections.Concurrent;
using System.Security.Authentication;
using SystemAgent.Core.Errors;

namespace SystemAgent.Infrastructure.Cluster;

/// <summary>
/// 他ノードへのmTLSクライアント。自ノードの証明書で認証し、相手のサーバー証明書は
/// 「クラスタCAで署名されている」かつ「証明書のノードIDが接続先として想定したノードである」場合のみ受け入れる。
/// 期待するノードIDをTLS検証に組み込むため、接続先ノードごとにクライアントを分ける。
/// </summary>
public sealed class ClusterHttpClientFactory : IDisposable
{
    public const string ActorHeader = "X-SystemAgent-Actor";

    private readonly ClusterIdentity _identity;
    private readonly ConcurrentDictionary<Guid, HttpClient> _clients = new();

    public ClusterHttpClientFactory(ClusterIdentity identity)
    {
        _identity = identity;
        _identity.Changed += Reset;
    }

    public HttpClient ClientFor(Guid nodeId) => _clients.GetOrAdd(nodeId, Create);

    private HttpClient Create(Guid expectedNodeId)
    {
        var identity = _identity.Current ?? throw new ClusterStateException("このノードはクラスタに参加していません。");
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            SslOptions =
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ClientCertificates = [identity.NodeCertificate],
                LocalCertificateSelectionCallback = (_, _, _, _, _) => identity.NodeCertificate,
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    certificate is not null
                    && Pki.ValidateNodeCertificate(certificate, identity.CaCertificate, NodeCertificateUsage.Server) == expectedNodeId,
            },
        };
        // タイムアウトは呼び出し側（プロキシ）のCancellationTokenで管理する
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private void Reset()
    {
        foreach (var key in _clients.Keys)
        {
            if (_clients.TryRemove(key, out var client)) client.Dispose();
        }
    }

    public void Dispose()
    {
        _identity.Changed -= Reset;
        Reset();
    }
}
