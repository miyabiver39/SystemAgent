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

        DateOnly? lastRun = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            // タイマーは予定より数ミリ秒早く戻ることがあるため少し余裕を持たせ、さらに同じ日の2回目は実行しない
            var delay = NextDelay(time.GetLocalNow(), at, lastRun);
            await Task.Delay(delay + Margin, time, stoppingToken);
            var today = DateOnly.FromDateTime(time.GetLocalNow().DateTime);
            if (today == lastRun) continue;
            lastRun = today;
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

    /// <summary>実行予定時刻を少し過ぎてから起きるための余裕。</summary>
    public static readonly TimeSpan Margin = TimeSpan.FromSeconds(1);

    /// <param name="lastRun">最後に実行した日（ローカル）。その日の予定時刻は過ぎたものとして翌日にする。</param>
    public static TimeSpan NextDelay(DateTimeOffset now, TimeOnly at, DateOnly? lastRun = null)
    {
        var today = DateOnly.FromDateTime(now.DateTime);
        var next = new DateTimeOffset(today.ToDateTime(at), now.Offset);
        if (next <= now || today == lastRun) next = next.AddDays(1);
        return next - now;
    }
}
