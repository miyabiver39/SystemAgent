using Microsoft.AspNetCore.Authentication;
using SystemAgent.Infrastructure.Cluster;
using SystemAgent.Web.Auth;

namespace SystemAgent.Web.Api;

/// <summary>
/// ノード間通信ポート（Cluster:Port、既定5443）で受け付ける要求を限る（ADR-018）。
/// <list type="bullet">
/// <item>クラスタCAが発行したノード証明書で認証された要求（他ノードからの転送）: API のみ受け付ける。</item>
/// <item>証明書なしで受け付けるのは、参加前のノードが使う CA証明書の取得（GET /api/cluster/ca）と参加（POST /api/cluster/enroll）だけ。</item>
/// </list>
/// WebUI・ログイン・初期セットアップ・アップロード等は WebUI/API のポートでだけ受け付け、ノード間通信ポートでは 404 にする。
/// </summary>
public static class ClusterPortGuard
{
    public static IApplicationBuilder UseClusterPortGuard(this IApplicationBuilder app)
    {
        var endpoint = app.ApplicationServices.GetRequiredService<ClusterEndpointSettings>();
        return app.Use(async (context, next) =>
        {
            if (endpoint.Port <= 0 || context.Connection.LocalPort != endpoint.Port)
            {
                await next(context);
                return;
            }

            if (IsAllowedWithoutCertificate(context.Request.Method, context.Request.Path))
            {
                await next(context);
                return;
            }

            var result = await context.AuthenticateAsync(NodeCertificateAuthenticationHandler.SchemeName);
            if (result.Succeeded && context.Request.Path.StartsWithSegments("/api"))
            {
                await next(context);
                return;
            }
            context.Response.StatusCode = StatusCodes.Status404NotFound;
        });
    }

    /// <summary>ノード証明書を持たない（参加前の）ノードに許す要求。</summary>
    public static bool IsAllowedWithoutCertificate(string method, PathString path) =>
        (HttpMethods.IsGet(method) && path.Equals("/api/cluster/ca", StringComparison.OrdinalIgnoreCase))
        || (HttpMethods.IsPost(method) && path.Equals("/api/cluster/enroll", StringComparison.OrdinalIgnoreCase));
}
