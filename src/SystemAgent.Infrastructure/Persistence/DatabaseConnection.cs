using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using SystemAgent.Core.Security;

namespace SystemAgent.Infrastructure.Persistence;

public enum DatabaseConnectionSource
{
    /// <summary>未設定。</summary>
    None,

    /// <summary>ローカル秘密情報（ホスト固有鍵で暗号化。WebUI/CLIから設定）。</summary>
    Secret,

    /// <summary>設定ファイル（appsettings / 環境変数 ConnectionStrings__Default）。</summary>
    Configuration,
}

/// <summary>
/// DB接続文字列の取得・変更（基本設計書 6章「秘密情報(DB接続情報)の変更機能」、ADR-003）。
/// 接続文字列はローカル秘密情報を優先し、無ければ設定ファイルを使う。DbContext生成のたびに読むため、変更は再起動なしで反映される。
/// </summary>
public sealed class DatabaseConnection(ILocalSecretStore secrets, IConfiguration configuration)
{
    public const string SecretName = "db.connectionString";
    public const int ConnectTimeoutSeconds = 5;

    public (string? ConnectionString, DatabaseConnectionSource Source) Current =>
        secrets.GetSecret(SecretName) is { Length: > 0 } secret
            ? (secret, DatabaseConnectionSource.Secret)
            : configuration.GetConnectionString("Default") is { Length: > 0 } configured && !configured.Contains("CHANGE_ME")
                ? (configured, DatabaseConnectionSource.Configuration)
                : (null, DatabaseConnectionSource.None);

    public bool IsConfigured => Current.ConnectionString is not null;

    /// <summary>
    /// 未設定時もDbContextを生成できるよう、到達しない接続先を返す。実際には UnconfiguredDatabaseInterceptor が
    /// 接続前に DatabaseNotConfiguredException で失敗させる（→ 503）。
    /// </summary>
    public string ConnectionStringOrPlaceholder =>
        Current.ConnectionString ?? $"Server=127.0.0.1;Port=1;Database=systemagent;Connection Timeout={ConnectTimeoutSeconds}";

    public static string Build(string server, int port, string database, string user, string password) =>
        new MySqlConnectionStringBuilder
        {
            Server = server,
            Port = (uint)port,
            Database = database,
            UserID = user,
            Password = password,
            ConnectionTimeout = ConnectTimeoutSeconds,
        }.ConnectionString;

    /// <summary>接続できるか試す。失敗時はエラーメッセージを返す。</summary>
    public static async Task<string?> TestAsync(string connectionString, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            return null;
        }
        catch (MySqlException ex)
        {
            return ex.Message;
        }
    }

    public void Save(string connectionString) => secrets.SetSecret(SecretName, connectionString);

    public static (string Server, int Port, string Database, string User) Describe(string connectionString)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString);
        return (builder.Server, (int)builder.Port, builder.Database, builder.UserID);
    }
}

public sealed class DatabaseMigrator(AppDbContext db)
{
    public async Task<IReadOnlyList<string>> PendingAsync(CancellationToken cancellationToken) =>
        (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();

    /// <summary>未適用のマイグレーションを適用する。複数ノードから同時に呼ばれてもEF Coreのロックで直列化される。</summary>
    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        await db.Database.MigrateAsync(cancellationToken);
        // 更新直後（列追加前）の記録失敗で待機中でも、すぐに監査ログの記録を再開する
        Auditing.DbAuditLogger.ResetBackoff();
    }
}
