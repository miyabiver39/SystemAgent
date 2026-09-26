using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SystemAgent.Core.Auditing;
using SystemAgent.Infrastructure.Persistence;
using SystemAgent.Infrastructure.Persistence.Entities;

namespace SystemAgent.Infrastructure.Auditing;

public sealed class DbAuditLogger(AppDbContext db, TimeProvider time, ILogger<DbAuditLogger> logger) : IAuditLogger
{
    // DB障害中に操作のたびに接続タイムアウトを待たせないよう、失敗後しばらくはDB記録を試みない
    private static readonly TimeSpan RetryBackoff = TimeSpan.FromSeconds(30);
    private static long _skipDbUntilTicks;

    public async Task LogAsync(string actor, string action, string? detail = null, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        if (now.UtcTicks < Interlocked.Read(ref _skipDbUntilTicks))
        {
            LogNotRecorded(null, actor, action, detail);
            return;
        }

        var entry = db.AuditLogs.Add(new AuditLogEntity
        {
            ActorUserName = actor,
            Action = action,
            Detail = detail,
            OccurredAt = now,
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // DB停止中（緊急認証での操作等）でも本来の操作は継続させる。記録できなかった事実はアプリログに残す。
            entry.State = EntityState.Detached;
            Interlocked.Exchange(ref _skipDbUntilTicks, (now + RetryBackoff).UtcTicks);
            LogNotRecorded(ex, actor, action, detail);
        }
    }

    private void LogNotRecorded(Exception? ex, string actor, string action, string? detail) =>
        logger.LogWarning(ex, "監査ログをDBに記録できませんでした: actor={Actor} action={Action} detail={Detail}", actor, action, detail);
}
