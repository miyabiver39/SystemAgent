namespace SystemAgent.Infrastructure.Persistence.Entities;

/// <summary>
/// 秘密情報変更操作等の操作ログ（ADR-009: 初期リリースはDB記録のみ、閲覧UIなし）。
/// </summary>
public class AuditLogEntity
{
    public long Id { get; set; }
    public required string ActorUserName { get; set; }
    public required string Action { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}
