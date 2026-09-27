namespace SystemAgent.Infrastructure.Persistence.Entities;

/// <summary>
/// 操作ログ（ADR-009。閲覧はADR-025）。
/// </summary>
public class AuditLogEntity
{
    public long Id { get; set; }
    public required string ActorUserName { get; set; }
    public required string Action { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>操作を実行したノード（中央DBに全ノードの記録が集まるため）。導入前の記録はnull。</summary>
    public string? NodeName { get; set; }
}
