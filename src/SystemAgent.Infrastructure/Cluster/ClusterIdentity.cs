using System.Security.Cryptography.X509Certificates;
using SystemAgent.Core.Security;

namespace SystemAgent.Infrastructure.Cluster;

/// <summary>
/// このノードのクラスタ上の身元（ノードID・ノード証明書・CA証明書）。ローカル秘密情報に保存し、DBには依存しない
/// （DB停止中でもノード間のmTLS認証を継続できるようにするため）。
/// </summary>
public sealed class ClusterIdentity(ILocalSecretStore secrets)
{
    public const string ClusterNameKey = "cluster.name";
    public const string NodeIdKey = "cluster.nodeId";
    public const string CaCertificateKey = "pki.ca.cert";
    public const string CaPrivateKeyKey = "pki.ca.key";
    public const string NodeCertificateKey = "pki.node.cert";
    public const string NodePrivateKeyKey = "pki.node.key";

    private readonly Lock _lock = new();
    private Snapshot? _snapshot;

    public sealed record Snapshot(
        string ClusterName, Guid NodeId, X509Certificate2 NodeCertificate, X509Certificate2 CaCertificate, bool IsCa);

    public event Action? Changed;

    /// <summary>クラスタに参加済みならその情報。未参加ならnull。</summary>
    public Snapshot? Current
    {
        get
        {
            lock (_lock)
            {
                if (_snapshot is not null) return _snapshot;
                if (secrets.GetSecret(NodeIdKey) is not { } nodeId) return null;
                _snapshot = new Snapshot(
                    secrets.GetSecret(ClusterNameKey) ?? "",
                    Guid.Parse(nodeId),
                    Pki.WithPrivateKey(secrets.GetSecret(NodeCertificateKey)!, secrets.GetSecret(NodePrivateKeyKey)!),
                    X509Certificate2.CreateFromPem(secrets.GetSecret(CaCertificateKey)!),
                    secrets.GetSecret(CaPrivateKeyKey) is not null);
                return _snapshot;
            }
        }
    }

    /// <summary>CAノードのみ: 署名用に秘密鍵付きのCA証明書を返す。</summary>
    public X509Certificate2 LoadCaWithPrivateKey() =>
        secrets.GetSecret(CaPrivateKeyKey) is { } key
            ? Pki.WithPrivateKey(secrets.GetSecret(CaCertificateKey)!, key)
            : throw new InvalidOperationException("このノードはクラスタCAではありません。");

    public void Save(string clusterName, Guid nodeId, string nodeCertificatePem, string nodePrivateKeyPem,
        string caCertificatePem, string? caPrivateKeyPem)
    {
        lock (_lock)
        {
            secrets.SetSecret(ClusterNameKey, clusterName);
            secrets.SetSecret(CaCertificateKey, caCertificatePem);
            secrets.SetSecret(CaPrivateKeyKey, caPrivateKeyPem);
            secrets.SetSecret(NodeCertificateKey, nodeCertificatePem);
            secrets.SetSecret(NodePrivateKeyKey, nodePrivateKeyPem);
            // ノードIDは最後に書く（Currentはこれの有無で参加済みか判断する）
            secrets.SetSecret(NodeIdKey, nodeId.ToString());
            _snapshot = null;
        }
        Changed?.Invoke();
    }
}
