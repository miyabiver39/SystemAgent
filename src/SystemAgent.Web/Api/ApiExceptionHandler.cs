using System.Data.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using SystemAgent.Core.Errors;

namespace SystemAgent.Web.Api;

/// <summary>
/// /api配下の未処理例外をProblemDetailsで返す。画面(Blazor)側の例外はfalseを返して既定のエラーページに任せる。
/// <list type="bullet">
/// <item>業務上の失敗（SystemAgentException）: 種類（ErrorKind）で決める。400 / 404 / 409 / 422 / 501 / 502</item>
/// <item>DB接続障害: 503（クライアントは緊急ログインへ誘導する）</item>
/// <item>引数の不正: 400 / 権限なし: 403 / コマンドのタイムアウト: 504 / それ以外: 500（詳細はログのみ）</item>
/// </list>
/// </summary>
public sealed class ApiExceptionHandler(IProblemDetailsService problemDetails, ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        // ハンドラ呼び出し時点でRequest.PathはUseExceptionHandlerのエラーパス(/Error)に書き換えられているため、元のパスを使う
        var originalPath = new PathString(context.Features.Get<IExceptionHandlerFeature>()?.Path ?? context.Request.Path);
        if (!originalPath.StartsWithSegments("/api")) return false;

        var (status, detail) = exception switch
        {
            SystemAgentException ex => (StatusFor(ex.Kind), ex.Message),
            _ when exception is DbException || exception.InnerException is DbException =>
                (StatusCodes.Status503ServiceUnavailable, "データベースに接続できません。DB復旧までは緊急ログインで操作してください。"),
            KeyNotFoundException => (StatusCodes.Status404NotFound, "対象が見つかりません（削除済みか、名前が違います）。"),
            UnauthorizedAccessException ex => (StatusCodes.Status403Forbidden, ex.Message),
            ArgumentException ex => (StatusCodes.Status400BadRequest, ex.Message),
            TimeoutException ex => (StatusCodes.Status504GatewayTimeout, ex.Message),
            _ => (StatusCodes.Status500InternalServerError, "サーバー内部でエラーが発生しました。"),
        };

        // CSV出力・バックアップのダウンロード等で本文を送り始めた後は、ステータスコードもヘッダも変えられない。
        // 書き換えようとすると別の例外で本来の原因が隠れるため、記録だけして上位（接続の中断）に任せる
        if (context.Response.HasStarted)
        {
            logger.LogError(exception, "API応答の送信中に例外が発生したため、応答を中断します: {Path}", originalPath);
            return false;
        }

        if (status == StatusCodes.Status500InternalServerError)
            logger.LogError(exception, "API処理中に未処理の例外が発生しました: {Path}", originalPath);
        else
            logger.LogWarning(exception, "API処理が失敗しました ({Status}): {Path}", status, originalPath);

        context.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            Exception = exception,
            ProblemDetails = new ProblemDetails { Status = status, Detail = detail },
        });
    }

    private static int StatusFor(ErrorKind kind) => kind switch
    {
        ErrorKind.InvalidInput => StatusCodes.Status400BadRequest,
        ErrorKind.NotFound => StatusCodes.Status404NotFound,
        ErrorKind.Conflict => StatusCodes.Status409Conflict,
        ErrorKind.OperationFailed => StatusCodes.Status422UnprocessableEntity,
        ErrorKind.NotSupported => StatusCodes.Status501NotImplemented,
        ErrorKind.Unreachable => StatusCodes.Status502BadGateway,
        _ => StatusCodes.Status500InternalServerError,
    };
}
