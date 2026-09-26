namespace SystemAgent.Core.Nodes;

/// <summary>
/// マルチノード管理におけるノード登録情報（基本設計書 4章）。
/// </summary>
public sealed record NodeInfo(
    Guid Id,
    string HostName,
    string IpAddress,
    OsInfo Os,
    NodeRole Role,
    DateTimeOffset RegisteredAt);

public enum NodeRole
{
    Master,
    Replica,
    Managed,
}

public sealed record OsInfo(string Distribution, string Version, string Architecture);
