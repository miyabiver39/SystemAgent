using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace SystemAgent.Web.Auth;

/// <summary>
/// 未認証で呼べるAPI（ログイン・緊急ログイン・初期セットアップ）のレート制限。
/// 接続元IPアドレスごとに、1分あたり Auth:RateLimitPerMinute 回（既定10回）まで。超えたら429。
/// WebUIからの要求はこのノード自身（ループバック）から来るため、WebUIの利用者全体で1つの枠を共有する。
/// </summary>
public static class RateLimitPolicies
{
    public const string Auth = "auth";

    public static IServiceCollection AddAuthRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var permitLimit = Math.Max(1, configuration.GetValue("Auth:RateLimitPerMinute", 10));
        return services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, cancellationToken) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
                await context.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>().TryWriteAsync(new ProblemDetailsContext
                {
                    HttpContext = context.HttpContext,
                    ProblemDetails =
                    {
                        Status = StatusCodes.Status429TooManyRequests,
                        Detail = "試行回数が多すぎます。しばらく待ってからやり直してください。",
                    },
                });
            };
            options.AddPolicy(Auth, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
        });
    }
}
