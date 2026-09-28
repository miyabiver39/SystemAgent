using System.Collections.Concurrent;

namespace SystemAgent.Web.Auth;

/// <summary>
/// ログインの連続失敗による一時的なロックアウト（アカウント単位、このノードのメモリ上）。
/// 同じアカウントで Auth:LockoutThreshold 回（既定5回）続けて失敗すると、Auth:LockoutMinutes 分（既定15分）はパスワードが
/// 正しくてもログインさせない。パスワードの総当たりを防ぐため。成功すると失敗回数を消す。
/// 接続元ごとの試行回数の上限は、別にレート制限（Program.cs の "auth" ポリシー）で掛ける。
/// </summary>
public sealed class LoginThrottle(IConfiguration configuration, TimeProvider time)
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Entry(int Failures, DateTimeOffset FirstFailureAt, DateTimeOffset? LockedUntil);

    public int Threshold => Math.Max(1, configuration.GetValue("Auth:LockoutThreshold", 5));

    public TimeSpan LockoutDuration => TimeSpan.FromMinutes(Math.Max(1, configuration.GetValue("Auth:LockoutMinutes", 15)));

    /// <summary>ロックアウト中なら残り時間。</summary>
    public TimeSpan? LockedFor(string key)
    {
        if (!_entries.TryGetValue(key, out var entry) || entry.LockedUntil is not { } until) return null;
        var remaining = until - time.GetUtcNow();
        return remaining > TimeSpan.Zero ? remaining : null;
    }

    /// <returns>この失敗でロックアウトされたらtrue。</returns>
    public bool RecordFailure(string key)
    {
        var now = time.GetUtcNow();
        var updated = _entries.AddOrUpdate(key,
            _ => Next(new Entry(0, now, null), now),
            (_, current) => Next(current, now));
        Prune(now);
        return updated.LockedUntil == now + LockoutDuration;
    }

    public void RecordSuccess(string key) => _entries.TryRemove(key, out _);

    private Entry Next(Entry current, DateTimeOffset now)
    {
        // 失敗を数える期間（ロックアウトの長さと同じ）を過ぎた古い失敗・解除済みのロックアウトは数え直す
        if (now - current.FirstFailureAt > LockoutDuration || current.LockedUntil <= now)
            current = new Entry(0, now, null);
        var failures = current.Failures + 1;
        return current with { Failures = failures, LockedUntil = failures >= Threshold ? now + LockoutDuration : current.LockedUntil };
    }

    // 存在しないユーザー名での試行でメモリが増え続けないよう、期限切れのものを消す
    private void Prune(DateTimeOffset now)
    {
        if (_entries.Count < 1000) return;
        foreach (var (key, entry) in _entries)
        {
            if ((entry.LockedUntil ?? entry.FirstFailureAt + LockoutDuration) < now) _entries.TryRemove(key, out _);
        }
    }
}
