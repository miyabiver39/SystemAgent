using Microsoft.Extensions.Logging;
using MySqlConnector;
using SystemAgent.Core.Errors;

namespace SystemAgent.Infrastructure.Persistence;

/// <summary>中央DBのメンテナンス（復元）中であることの共有。実装は MaintenanceLock。</summary>
public interface IMaintenanceLock
{
    /// <summary>メンテナンス中にする。ほかで実行中なら ClusterStateException（409）。戻り値を破棄すると解除する。</summary>
    Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken);

    /// <summary>メンテナンス中か（このノードまたは他ノードが復元中）。確認できなければ false（操作は止めない）。</summary>
    Task<bool> IsActiveAsync(CancellationToken cancellationToken);
}

/// <summary>
/// 中央DBのメンテナンス（バックアップからの復元）中であることを、全ノードで共有する印。
/// 中央DBの名前付きロック（GET_LOCK）を、復元している間だけ専用の接続で持ち続ける。
/// 各ノードは更新系の操作の前に IS_FREE_LOCK で確かめ、復元中なら受け付けない（他ノードの書き込みで復元結果が崩れないように）。
/// 復元しているノードが落ちた場合も、接続が切れればロックは自動で外れる。
/// </summary>
public sealed class MaintenanceLock(DatabaseConnection connection, TimeProvider time, ILogger<MaintenanceLock> logger) : IMaintenanceLock
{
    public const string LockName = "systemagent.maintenance";

    /// <summary>確認結果を使い回す時間（更新のたびに中央DBへ問い合わせない）。</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(2);

    /// <summary>確認できなかった（DB停止中等）ときに、次に確認するまでの時間。DB停止中の操作を毎回待たせない。</summary>
    public static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(30);

    private readonly Lock _lock = new();
    private int _heldHere;
    private ILogger Logger => logger;
    private (bool Active, DateTimeOffset NextCheck) _cached;

    public async Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        var connectionString = connection.Current.ConnectionString
            ?? throw new CapabilityUnavailableException("DB接続が設定されていません。");
        var db = new MySqlConnection(connectionString);
        try
        {
            await db.OpenAsync(cancellationToken);
            await using var command = new MySqlCommand("SELECT GET_LOCK(@name, 0)", db);
            command.Parameters.AddWithValue("@name", LockName);
            if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) != 1)
                throw new ClusterStateException("他のノードでデータベースの復元が実行中です。完了してから操作してください。");
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }

        Interlocked.Increment(ref _heldHere);
        logger.LogWarning("中央DBをメンテナンス中にしました（他ノードからの更新を受け付けません）。");
        return new Releaser(this, db);
    }

    public async Task<bool> IsActiveAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _heldHere) > 0) return true;
        if (connection.Current.ConnectionString is not { } connectionString) return false;

        var now = time.GetUtcNow();
        lock (_lock)
        {
            if (now < _cached.NextCheck) return _cached.Active;
        }

        bool active;
        TimeSpan next;
        try
        {
            // DB停止中に長く待たせないよう、接続タイムアウトを短くする
            var builder = new MySqlConnectionStringBuilder(connectionString) { ConnectionTimeout = 2 };
            await using var db = new MySqlConnection(builder.ConnectionString);
            await db.OpenAsync(cancellationToken);
            await using var command = new MySqlCommand("SELECT IS_FREE_LOCK(@name)", db);
            command.Parameters.AddWithValue("@name", LockName);
            active = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 0;
            next = CheckInterval;
        }
        catch (MySqlException ex)
        {
            logger.LogDebug(ex, "中央DBのメンテナンス状態を確認できませんでした。");
            active = false;
            next = FailureBackoff;
        }

        lock (_lock)
        {
            _cached = (active, now + next);
        }
        return active;
    }

    private sealed class Releaser(MaintenanceLock owner, MySqlConnection db) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var command = new MySqlCommand("SELECT RELEASE_LOCK(@name)", db);
                command.Parameters.AddWithValue("@name", LockName);
                await command.ExecuteScalarAsync();
            }
            catch (MySqlException ex)
            {
                // 接続が切れていればロックはすでに外れている
                owner.Logger.LogWarning(ex, "中央DBのメンテナンスの解除でエラーが発生しました（接続を閉じて解除します）。");
            }
            finally
            {
                await db.DisposeAsync();
                Interlocked.Decrement(ref owner._heldHere);
                lock (owner._lock) owner._cached = default;
                owner.Logger.LogWarning("中央DBのメンテナンスを解除しました。");
            }
        }
    }
}
