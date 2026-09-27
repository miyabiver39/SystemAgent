using System.Data.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using SystemAgent.Core.CapabilityProviders;
using SystemAgent.Core.Deploy;
using SystemAgent.Infrastructure.Cluster;

namespace SystemAgent.Web.Api;

/// <summary>
/// /api配下の未処理例外をProblemDetailsで返す。画面(Blazor)側の例外はfalseを返して既定のエラーページに任せる。
/// <list type="bullet">
/// <item>DB接続障害: 503（クライアントは緊急ログインへ誘導する）</item>
/// <item>OSコマンドの失敗・デプロイの失敗: 422（コマンドのエラー出力をそのまま返す）</item>
/// <item>対象がない: 404</item>
/// <item>この環境で提供できない機能: 501</item>
/// <item>引数の不正: 400 / コマンドのタイムアウト: 504</item>
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
            DatabaseOperationException ex => (StatusCodes.Status422UnprocessableEntity, ex.Message),
            DeploymentFailedException ex => (StatusCodes.Status422UnprocessableEntity, ex.Message),
            RegistryRequestException { Unreachable: true } ex => (StatusCodes.Status502BadGateway, ex.Message),
            RegistryRequestException ex => (StatusCodes.Status422UnprocessableEntity, ex.Message),
            KeyNotFoundException ex when ex.Message.EndsWith('。') => (StatusCodes.Status404NotFound, ex.Message),
            KeyNotFoundException => (StatusCodes.Status404NotFound, "対象が見つかりません（削除済みか、名前が違います）。"),
            _ when exception is DbException || exception.InnerException is DbException =>
                (StatusCodes.Status503ServiceUnavailable, "データベースに接続できません。DB復旧までは緊急ログインで操作してください。"),
            CommandFailedException ex => (StatusCodes.Status422UnprocessableEntity, ex.Message),
            CapabilityUnavailableException ex => (StatusCodes.Status501NotImplemented, ex.Message),
            ClusterStateException ex => (StatusCodes.Status409Conflict, ex.Message),
            UnauthorizedAccessException ex => (StatusCodes.Status403Forbidden, ex.Message),
            ArgumentException ex => (StatusCodes.Status400BadRequest, ex.Message),
            TimeoutException ex => (StatusCodes.Status504GatewayTimeout, ex.Message),
            _ => (StatusCodes.Status500InternalServerError, "サーバー内部でエラーが発生しました。"),
        };

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
}
