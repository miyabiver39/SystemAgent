using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SystemAgent.Infrastructure.Cluster;
using SystemAgent.Web.Api;
using SystemAgent.Web.Auth;

namespace SystemAgent.Web.Controllers;

/// <summary>
/// 他ノードの操作（基本設計書 2.1節「任意の1台から他ノードを操作可能」）。
/// /api/nodes/{nodeId}/proxy/api/... を、対象ノードの /api/... にmTLSで転送する。
/// 既存の全機能（コンテナ・NTP・ネットワーク等）を機能ごとの実装なしに他ノードで使えるようにするため、汎用の転送にしている。
/// </summary>
[ApiController]
[Authorize]
public class NodeProxyController(NodeForwarder forwarder, ClusterEndpointSettings endpoint) : ControllerBase
{
    private static readonly HashSet<string> ForwardedResponseHeaders =
        new(StringComparer.OrdinalIgnoreCase) { "Content-Type", "Content-Disposition" };

    [Route("api/nodes/{nodeId:guid}/proxy/{**path}")]
    [AcceptVerbs("GET", "POST", "PUT", "DELETE", "PATCH")]
    [DisableRequestSizeLimit]
    [DisableFormValueModelBinding]
    public async Task<IActionResult> Proxy(Guid nodeId, string path, CancellationToken cancellationToken)
    {
        // 転送先で再度転送させない（ループ防止）。操作対象はAPIのみ
        if (!path.StartsWith("api/", StringComparison.Ordinal) || path.StartsWith("api/nodes/", StringComparison.Ordinal))
            return Problem(statusCode: StatusCodes.Status400BadRequest, detail: "転送できないパスです。");

        HttpContent? content = null;
        if (Request.ContentLength > 0 || Request.Headers.TransferEncoding.Count > 0)
        {
            content = new StreamContent(Request.Body);
            if (Request.ContentType is { } contentType) content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            if (Request.ContentLength is { } length) content.Headers.ContentLength = length;
        }

        using (var response = await forwarder.SendAsync(nodeId, new HttpMethod(Request.Method), $"{path}{Request.QueryString}", content,
                   Request.Headers.Accept.ToString(), Actor(), cancellationToken))
        {
            Response.StatusCode = (int)response.StatusCode;
            foreach (var header in response.Content.Headers.Concat(response.Headers).Where(h => ForwardedResponseHeaders.Contains(h.Key)))
            {
                Response.Headers[header.Key] = header.Value.ToArray();
            }
            await response.Content.CopyToAsync(Response.Body, cancellationToken);
        }
        return new EmptyResult();
    }

    /// <summary>転送先の監査ログに残す操作者。すでに他ノード経由の操作者名ならそのまま。</summary>
    private string Actor()
    {
        var name = User.Identity!.Name!;
        return User.FindFirst(AuthConstants.AuthSourceClaim)?.Value == "node" ? name : $"{name}@{endpoint.NodeName}";
    }
}
