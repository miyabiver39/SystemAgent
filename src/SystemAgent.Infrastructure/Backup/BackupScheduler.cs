using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SystemAgent.Infrastructure.Backup;

/// <summary>
/// 定時バックアップ（Backup:DailyAt = "HH:mm"、このノードのローカル時刻）。未設定なら何もしない。
/// 中央DBは全ノード共通のため、定時実行はクラスタ内の1台だけで設定する。
/// </summary>
public sealed class BackupScheduler(BackupService backups, TimeProvider time, ILogger<BackupScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (backups.DailyAt is not { } dailyAt) return;
        if (!TimeOnly.TryParseExact(dailyAt, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
        {
            logger.LogError("Backup:DailyAt の形式が不正です（HH:mm で指定）: {Value}", dailyAt);
            return;
        }
        logger.LogInformation("定時バックアップを有効にしました（毎日 {At}）。", dailyAt);

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = NextDelay(time.GetLocalNow(), at);
            await Task.Delay(delay, time, stoppingToken);
            try
            {
                await backups.CreateAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "定時バックアップに失敗しました。");
            }
        }
    }

    public static TimeSpan NextDelay(DateTimeOffset now, TimeOnly at)
    {
        var next = new DateTimeOffset(DateOnly.FromDateTime(now.DateTime).ToDateTime(at), now.Offset);
        if (next <= now) next = next.AddDays(1);
        return next - now;
    }
}
