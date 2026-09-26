namespace SystemAgent.Core.Nodes;

/// <summary>
/// マルチノード管理におけるノード登録情報（基本設計書 4章）。
/// </summary>
/// <param name="IpAddress">他ノードから到達するアドレス（ノード間通信の接続先）。</param>
/// <param name="ClusterPort">ノード間通信（mTLS）のポート。</param>
/// <param name="CertificateNotAfter">ノード証明書の有効期限。</param>
public sealed record NodeInfo(
    Guid Id,
    string HostName,
    string IpAddress,
    int ClusterPort,
    OsInfo Os,
    NodeRole Role,
    DateTimeOffset RegisteredAt,
    DateTimeOffset? CertificateNotAfter);

public enum NodeRole
{
    Master,
    Replica,
    Managed,
}

public sealed record OsInfo(string Distribution, string Version, string Architecture);
