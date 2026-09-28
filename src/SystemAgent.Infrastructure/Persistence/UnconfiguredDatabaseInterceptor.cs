using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace SystemAgent.Infrastructure.Persistence;

/// <summary>DB接続が設定されていない。DB接続障害と同じく DbException として扱う（APIは503、監査ログはDBに記録しない）。</summary>
public sealed class DatabaseNotConfiguredException()
    : DbException("DB接続が設定されていません。緊急ログインして、設定画面からDB接続を設定してください。");

/// <summary>
/// DB接続が未設定のときに使う DbContext の接続を、開く前に失敗させる。
/// 到達しない接続先へ接続を試みて、接続タイムアウト（5秒）まで要求を待たせないため。
/// </summary>
public sealed class UnconfiguredDatabaseInterceptor : DbConnectionInterceptor
{
    public static readonly UnconfiguredDatabaseInterceptor Instance = new();

    private UnconfiguredDatabaseInterceptor()
    {
    }

    public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result) =>
        throw new DatabaseNotConfiguredException();

    public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
        DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) =>
        throw new DatabaseNotConfiguredException();
}
