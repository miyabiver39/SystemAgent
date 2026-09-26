namespace SystemAgent.Core.Auditing;

/// <summary>
/// 操作ログ（ADR-009）。DB停止中は記録できないが、呼び出し元の操作は失敗させない。
/// </summary>
public interface IAuditLogger
{
    Task LogAsync(string actor, string action, string? detail = null, CancellationToken cancellationToken = default);
}
