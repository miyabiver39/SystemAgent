using MySqlConnector;
using SystemAgent.Core.CapabilityProviders;
using SystemAgent.Core.Ha;

namespace SystemAgent.Infrastructure.Ha;

/// <summary>
/// このノードのMariaDB（中央DBのマスター/レプリカ）の役割を切り替える（基本設計書 4.2節）。
/// 中央DBの接続（VIP経由）ではなく、このノード自身のDBに管理者権限で接続する。
/// 前提: log_bin・一意な server_id・GTID が有効なレプリケーション構成（ADR-022）。
/// </summary>
public sealed class DbRoleManager
{
    public async Task<DbRoleStatus> GetStatusAsync(string connectionString, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await OpenAsync(connectionString, cancellationToken);
            var readOnly = Convert.ToInt32(await ScalarAsync(connection, "SELECT @@GLOBAL.read_only", cancellationToken)) == 1;
            var gtid = (await ScalarAsync(connection, "SELECT @@GLOBAL.gtid_current_pos", cancellationToken))?.ToString();
            var serverId = Convert.ToInt32(await ScalarAsync(connection, "SELECT @@GLOBAL.server_id", cancellationToken));
            var problems = await PrerequisiteProblemsAsync(connection, cancellationToken);

            await using var command = new MySqlCommand("SHOW SLAVE STATUS", connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return new DbRoleStatus(true, null, readOnly, false, false, null, null, null, null, gtid, serverId, problems);

            string? Get(string column) => reader[column] is DBNull ? null : reader[column]?.ToString();
            var io = Get("Slave_IO_Running");
            var sql = Get("Slave_SQL_Running");
            var lastError = new[] { Get("Last_IO_Error"), Get("Last_SQL_Error") }.FirstOrDefault(e => !string.IsNullOrEmpty(e));
            return new DbRoleStatus(true, null, readOnly, true, io == "Yes" && sql == "Yes",
                Get("Master_Host"), int.TryParse(Get("Master_Port"), out var port) ? port : null,
                int.TryParse(Get("Seconds_Behind_Master"), out var behind) ? behind : null, lastError, gtid, serverId, problems);
        }
        catch (Exception ex) when (ex is MySqlException or DatabaseOperationException)
        {
            return new DbRoleStatus(false, ex.Message, false, false, false, null, null, null, null, null);
        }
    }

    /// <summary>マスターに昇格する: 複製を止めて設定を消し、書き込みを許可する。</summary>
    public async Task PromoteAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(connectionString, cancellationToken);
        await ExecuteAsync(connection, "STOP SLAVE", cancellationToken);
        await ExecuteAsync(connection, "RESET SLAVE ALL", cancellationToken);
        await ExecuteAsync(connection, "SET GLOBAL read_only = OFF", cancellationToken);
    }

    /// <summary>書き込みを禁止する（VIPを失ったノードのスプリットブレイン対策）。</summary>
    public async Task SetReadOnlyAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(connectionString, cancellationToken);
        await ExecuteAsync(connection, "SET GLOBAL read_only = ON", cancellationToken);
    }

    /// <summary>
    /// フェイルオーバーの前提条件の不足を返す。
    /// <list type="bullet">
    /// <item>log_bin: 昇格後にマスターとしてバイナリログを出すため。</item>
    /// <item>log_slave_updates: レプリカのバイナリログにも全トランザクションを残す。これが無いと、元マスターが再参加する際に
    /// 新マスター側で開始位置のGTIDが見つからない。</item>
    /// <item>gtid_strict_mode: 食い違ったトランザクションを黙って適用せず、エラーで止める（データの不整合を防ぐ）。</item>
    /// </list>
    /// </summary>
    private static async Task<IReadOnlyList<string>> PrerequisiteProblemsAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        foreach (var (variable, reason) in new[]
                 {
                     ("log_bin", "バイナリログが無効です（log_bin）"),
                     ("log_slave_updates", "log_slave_updates が無効です（レプリカ側にもバイナリログを残す必要があります）"),
                     ("gtid_strict_mode", "gtid_strict_mode が無効です（食い違いを検出できません）"),
                 })
        {
            if (Convert.ToInt32(await ScalarAsync(connection, $"SELECT @@GLOBAL.{variable}", cancellationToken)) != 1) problems.Add(reason);
        }
        return problems;
    }

    /// <summary>
    /// 指定したマスターのレプリカとして（再）参加する。
    /// MASTER_USE_GTID=current_pos: 自分のバイナリログ（元マスターとして書いたものを含む）の位置から続ける。
    /// slave_pos だと元マスターは位置が空または古く、新マスターのバイナリログを先頭から再生してデータを巻き戻してしまう。
    /// </summary>
    public async Task JoinAsReplicaAsync(string connectionString, string sourceHost, int sourcePort, string user, string password,
        CancellationToken cancellationToken)
    {
        // 接続先が書き込み可能なマスターであることを確かめる。昇格に失敗したレプリカを接続先にすると、互いに複製し合う循環になる
        var source = new MySqlConnectionStringBuilder { Server = sourceHost, Port = (uint)sourcePort, UserID = user, Password = password, ConnectionTimeout = 5 };
        await using (var sourceConnection = await OpenAsync(source.ConnectionString, cancellationToken))
        {
            if (Convert.ToInt32(await ScalarAsync(sourceConnection, "SELECT @@GLOBAL.read_only", cancellationToken)) == 1)
                throw new DatabaseOperationException($"接続先 {sourceHost}:{sourcePort} が読み取り専用のため、マスターになっていません。再参加を中止しました。");
        }

        await using var connection = await OpenAsync(connectionString, cancellationToken);
        await ExecuteAsync(connection, "SET GLOBAL read_only = ON", cancellationToken);
        await ExecuteAsync(connection, "STOP SLAVE", cancellationToken);
        await using (var change = new MySqlCommand(
            "CHANGE MASTER TO MASTER_HOST=@host, MASTER_PORT=@port, MASTER_USER=@user, MASTER_PASSWORD=@password, " +
            "MASTER_USE_GTID=current_pos, MASTER_CONNECT_RETRY=10", connection))
        {
            change.Parameters.AddWithValue("@host", sourceHost);
            change.Parameters.AddWithValue("@port", sourcePort);
            change.Parameters.AddWithValue("@user", user);
            change.Parameters.AddWithValue("@password", password);
            await change.ExecuteNonQueryAsync(cancellationToken);
        }
        await ExecuteAsync(connection, "START SLAVE", cancellationToken);
    }

    private static async Task<MySqlConnection> OpenAsync(string connectionString, CancellationToken cancellationToken)
    {
        var connection = new MySqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch (MySqlException ex)
        {
            await connection.DisposeAsync();
            var target = new MySqlConnectionStringBuilder(connectionString);
            throw new DatabaseOperationException($"DB {target.UserID}@{target.Server}:{target.Port} に接続できません: {ex.Message}", ex);
        }
    }

    private static async Task<object?> ScalarAsync(MySqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task ExecuteAsync(MySqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = new MySqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (MySqlException ex)
        {
            throw new DatabaseOperationException($"DBの操作（{sql}）に失敗しました: {ex.Message}", ex);
        }
    }
}
