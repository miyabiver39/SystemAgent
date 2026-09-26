using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SystemAgent.Core.Nodes;
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
public class NodeProxyController(
    INodeService nodes, ClusterIdentity identity, ClusterHttpClientFactory clients, ClusterEndpointSettings endpoint,
    ILogger<NodeProxyController> logger) : ControllerBase
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
        if (identity.Current is null)
            return Problem(statusCode: StatusCodes.Status409Conflict, detail: "このノードはクラスタに参加していないため、他ノードを操作できません。");

        var node = await nodes.GetAsync(nodeId, cancellationToken);
        if (node is null) return Problem(statusCode: StatusCodes.Status404NotFound, detail: "指定されたノードは登録されていません。");

        var host = node.IpAddress.Contains(':') ? $"[{node.IpAddress}]" : node.IpAddress;
        using var request = new HttpRequestMessage(new HttpMethod(Request.Method), $"https://{host}:{node.ClusterPort}/{path}{Request.QueryString}");
        if (Request.ContentLength > 0 || Request.Headers.TransferEncoding.Count > 0)
        {
            request.Content = new StreamContent(Request.Body);
            if (Request.ContentType is { } contentType) request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            if (Request.ContentLength is { } length) request.Content.Headers.ContentLength = length;
        }
        request.Headers.TryAddWithoutValidation("Accept", Request.Headers.Accept.ToString());
        request.Headers.Add(ClusterHttpClientFactory.ActorHeader, Actor());

        HttpResponseMessage response;
        try
        {
            response = await clients.ClientFor(nodeId).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "ノード {Node}（{Address}:{Port}）への転送に失敗しました", node.HostName, node.IpAddress, node.ClusterPort);
            return Problem(statusCode: StatusCodes.Status502BadGateway,
                detail: $"ノード {node.HostName}（{node.IpAddress}:{node.ClusterPort}）に接続できません: {ex.Message}");
        }

        using (response)
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
