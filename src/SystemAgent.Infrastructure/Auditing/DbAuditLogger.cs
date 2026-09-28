using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SystemAgent.Core.Auditing;
using SystemAgent.Infrastructure.Cluster;
using SystemAgent.Infrastructure.Persistence;
using SystemAgent.Infrastructure.Persistence.Entities;

namespace SystemAgent.Infrastructure.Auditing;

/// <summary>
/// 監査ログを中央DBに記録する。記録には要求ごとの DbContext とは別の DbContext を使う。
/// 同じ DbContext だと、業務処理の保存に失敗した変更が監査ログの保存で一緒に書き込まれたり、
/// 監査ログの保存の失敗が業務処理の変更追跡に残ったりして、互いに干渉するため。
/// </summary>
public sealed class DbAuditLogger(
    DbContextOptions<AppDbContext> options, TimeProvider time, ClusterEndpointSettings endpoint, ILogger<DbAuditLogger> logger)
    : IAuditLogger
{
    // DB障害中に操作のたびに接続タイムアウトを待たせないよう、失敗後しばらくはDB記録を試みない
    private static readonly TimeSpan RetryBackoff = TimeSpan.FromSeconds(30);
    private static long _skipDbUntilTicks;

    /// <summary>DB復旧・マイグレーション直後に、待機せず記録を再開する。</summary>
    public static void ResetBackoff() => Interlocked.Exchange(ref _skipDbUntilTicks, 0);

    public async Task LogAsync(string actor, string action, string? detail = null, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        if (now.UtcTicks < Interlocked.Read(ref _skipDbUntilTicks))
        {
            LogNotRecorded(null, actor, action, detail);
            return;
        }

        try
        {
            await using var db = new AppDbContext(options);
            db.AuditLogs.Add(new AuditLogEntity
            {
                ActorUserName = actor,
                Action = action,
                Detail = detail,
                OccurredAt = now,
                NodeName = endpoint.NodeName,
            });
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // DB停止中（緊急認証での操作等）でも本来の操作は継続させる。記録できなかった事実はアプリログに残す。
            Interlocked.Exchange(ref _skipDbUntilTicks, (now + RetryBackoff).UtcTicks);
            LogNotRecorded(ex, actor, action, detail);
        }
    }

    private void LogNotRecorded(Exception? ex, string actor, string action, string? detail) =>
        logger.LogWarning(ex, "監査ログをDBに記録できませんでした: actor={Actor} action={Action} detail={Detail}", actor, action, detail);
}
