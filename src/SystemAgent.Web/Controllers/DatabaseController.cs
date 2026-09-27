using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SystemAgent.Core.Auditing;
using SystemAgent.Core.Contracts;
using SystemAgent.Infrastructure.Persistence;
using SystemAgent.Web.Auth;

namespace SystemAgent.Web.Controllers;

/// <summary>
/// このノードのDB接続設定（基本設計書 6章「DB接続情報の変更機能」）。
/// 接続情報はホスト固有鍵で暗号化してローカルに保存する（ADR-003）。DB停止中でも緊急認証で操作できる。
/// </summary>
[ApiController]
[Authorize]
[Route("api/system/database")]
public class DatabaseController(
    DatabaseConnection connection, IServiceProvider services, IAuditLogger audit) : ControllerBase
{
    [HttpGet]
    public async Task<DatabaseSettingsResponse> Get(CancellationToken cancellationToken)
    {
        var (connectionString, source) = connection.Current;
        if (connectionString is null)
            return new DatabaseSettingsResponse(source.ToString(), null, null, null, null, false, "DB接続が設定されていません。", []);

        var (server, port, database, user) = DatabaseConnection.Describe(connectionString);
        var error = await DatabaseConnection.TestAsync(connectionString, cancellationToken);
        var pending = error is null ? await MigratorAsync(m => m.PendingAsync(cancellationToken)) : [];
        return new DatabaseSettingsResponse(source.ToString(), server, port, database, user, error is null, error, pending);
    }

    /// <summary>接続を試して成功した場合のみ保存し、未適用のマイグレーション（テーブル作成・変更）を適用する。</summary>
    [HttpPut]
    public async Task<IActionResult> Set(SetDatabaseRequest request, CancellationToken cancellationToken)
    {
        var connectionString = DatabaseConnection.Build(request.Server, request.Port, request.Database, request.User, request.Password);
        if (await DatabaseConnection.TestAsync(connectionString, cancellationToken) is { } error)
            return Problem(statusCode: StatusCodes.Status422UnprocessableEntity, detail: $"データベースに接続できません: {error}");

        connection.Save(connectionString);
        await MigratorAsync(async m => { await m.MigrateAsync(cancellationToken); return 0; });
        await audit.LogAsync(User.ActorName(), "secret.database.set",
            $"{request.User}@{request.Server}:{request.Port}/{request.Database}", cancellationToken);
        return NoContent();
    }

    /// <summary>未適用のマイグレーションを適用する（バージョンアップ後など）。</summary>
    [HttpPost("migrate")]
    public async Task<IActionResult> Migrate(CancellationToken cancellationToken)
    {
        var pending = await MigratorAsync(m => m.PendingAsync(cancellationToken));
        await MigratorAsync(async m => { await m.MigrateAsync(cancellationToken); return 0; });
        await audit.LogAsync(User.ActorName(), "database.migrate", string.Join(", ", pending), cancellationToken);
        return NoContent();
    }

    // 接続文字列の変更後に新しい設定でDbContextを作るため、要求ごとにスコープを作る
    private async Task<T> MigratorAsync<T>(Func<DatabaseMigrator, Task<T>> action)
    {
        await using var scope = services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<DatabaseMigrator>());
    }
}
