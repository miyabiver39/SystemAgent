using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SystemAgent.Core.Auditing;
using SystemAgent.Infrastructure.Persistence;
using SystemAgent.Infrastructure.Persistence.Entities;

namespace SystemAgent.Infrastructure.Auditing;

public sealed class DbAuditLogger(AppDbContext db, TimeProvider time, ILogger<DbAuditLogger> logger) : IAuditLogger
{
    public async Task LogAsync(string actor, string action, string? detail = null, CancellationToken cancellationToken = default)
    {
        var entry = db.AuditLogs.Add(new AuditLogEntity
        {
            ActorUserName = actor,
            Action = action,
            Detail = detail,
            OccurredAt = time.GetUtcNow(),
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // DB停止中（緊急認証での操作等）でも本来の操作は継続させる。記録できなかった事実はアプリログに残す。
            entry.State = EntityState.Detached;
            logger.LogWarning(ex, "監査ログをDBに記録できませんでした: actor={Actor} action={Action} detail={Detail}", actor, action, detail);
        }
    }
}
