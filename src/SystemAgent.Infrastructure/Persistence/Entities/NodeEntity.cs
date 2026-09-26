using SystemAgent.Core.Nodes;

namespace SystemAgent.Infrastructure.Persistence.Entities;

/// <summary>
/// ノード登録情報の永続化用エンティティ（基本設計書 4章）。
/// </summary>
public class NodeEntity
{
    public Guid Id { get; set; }
    public required string HostName { get; set; }
    public required string IpAddress { get; set; }
    public required string OsDistribution { get; set; }
    public required string OsVersion { get; set; }
    public required string OsArchitecture { get; set; }
    public NodeRole Role { get; set; }
    public DateTimeOffset RegisteredAt { get; set; }
}
