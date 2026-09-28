using SystemAgent.Infrastructure.Persistence;

namespace SystemAgent.Web.Api;

/// <summary>
/// 中央DBの復元中（MaintenanceLock）は、全ノードで更新系のAPI（POST / PUT / PATCH / DELETE）を 503 で断る。
/// 復元中に監査ログ・ユーザー・クラスタ等が書き込まれて、復元の結果が崩れたり食い違ったりしないようにするため。
/// ただしログイン・初期セットアップと、keepalived からの状態通知（HAの切り替えは止められない）は受け付ける。
/// </summary>
public static class MaintenanceGuard
{
    private static readonly string[] AlwaysAllowed = ["/api/auth/login", "/api/auth/emergency-login", "/api/setup", "/api/ha/notify"];

    public static IApplicationBuilder UseMaintenanceGuard(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (IsGuarded(context.Request.Method, context.Request.Path)
            && await context.RequestServices.GetRequiredService<IMaintenanceLock>().IsActiveAsync(context.RequestAborted))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.RetryAfter = "30";
            await context.RequestServices.GetRequiredService<IProblemDetailsService>().TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = context,
                ProblemDetails =
                {
                    Status = StatusCodes.Status503ServiceUnavailable,
                    Detail = "データベースの復元中のため、変更の操作はできません。復元が終わってからやり直してください。",
                },
            });
            return;
        }
        await next(context);
    });

    /// <summary>復元中に断る要求か。</summary>
    public static bool IsGuarded(string method, PathString path) =>
        path.StartsWithSegments("/api")
        && !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method))
        && !AlwaysAllowed.Any(allowed => path.Equals(allowed, StringComparison.OrdinalIgnoreCase));
}
