using System.Data.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace SystemAgent.Web.Api;

/// <summary>
/// /api配下の未処理例外をProblemDetailsで返す。DB接続障害は503とし、クライアントを緊急ログインへ誘導できるようにする。
/// 画面(Blazor)側の例外はfalseを返して既定のエラーページに任せる。
/// </summary>
public sealed class ApiExceptionHandler(IProblemDetailsService problemDetails, ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        // ハンドラ呼び出し時点でRequest.PathはUseExceptionHandlerのエラーパス(/Error)に書き換えられているため、元のパスを使う
        var originalPath = new PathString(context.Features.Get<IExceptionHandlerFeature>()?.Path ?? context.Request.Path);
        if (!originalPath.StartsWithSegments("/api")) return false;

        var databaseUnavailable = exception is DbException || exception.InnerException is DbException;
        if (databaseUnavailable)
            logger.LogWarning(exception, "API処理中にDBへ接続できませんでした: {Path}", originalPath);
        else
            logger.LogError(exception, "API処理中に未処理の例外が発生しました: {Path}", originalPath);

        context.Response.StatusCode = databaseUnavailable
            ? StatusCodes.Status503ServiceUnavailable
            : StatusCodes.Status500InternalServerError;

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = context.Response.StatusCode,
                Detail = databaseUnavailable
                    ? "データベースに接続できません。DB復旧までは緊急ログインで操作してください。"
                    : "サーバー内部でエラーが発生しました。",
            },
        });
    }
}
