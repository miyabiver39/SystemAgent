using SystemAgent.Core.Nodes;
using SystemAgent.Infrastructure.Cluster;

namespace SystemAgent.Web.Api;

/// <summary>他ノードのAPIへmTLSで要求を転送する（ADR-018）。</summary>
public sealed class NodeForwarder(
    INodeService nodes, ClusterIdentity identity, ClusterHttpClientFactory clients, ILogger<NodeForwarder> logger)
{
    /// <summary>転送できなかった（クラスタ未参加・ノード未登録・接続不可）。StatusCodeはそのまま応答に使う。</summary>
    public sealed class ForwardException(int statusCode, string message) : Exception(message)
    {
        public int StatusCode { get; } = statusCode;
    }

    /// <param name="path">転送先のパス（api/... ）とクエリ文字列。</param>
    /// <param name="actor">転送先の監査ログに残す操作者。</param>
    public async Task<HttpResponseMessage> SendAsync(
        Guid nodeId, HttpMethod method, string path, HttpContent? content, string? accept, string actor, CancellationToken cancellationToken)
    {
        if (identity.Current is null)
            throw new ForwardException(StatusCodes.Status409Conflict, "このノードはクラスタに参加していないため、他ノードを操作できません。");

        var node = await nodes.GetAsync(nodeId, cancellationToken)
            ?? throw new ForwardException(StatusCodes.Status404NotFound, "指定されたノードは登録されていません。");

        var host = node.IpAddress.Contains(':') ? $"[{node.IpAddress}]" : node.IpAddress;
        using var request = new HttpRequestMessage(method, $"https://{host}:{node.ClusterPort}/{path}") { Content = content };
        if (accept is { Length: > 0 }) request.Headers.TryAddWithoutValidation("Accept", accept);
        request.Headers.Add(ClusterHttpClientFactory.ActorHeader, actor);

        try
        {
            return await clients.ClientFor(nodeId).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "ノード {Node}（{Address}:{Port}）への転送に失敗しました", node.HostName, node.IpAddress, node.ClusterPort);
            throw new ForwardException(StatusCodes.Status502BadGateway,
                $"ノード {node.HostName}（{node.IpAddress}:{node.ClusterPort}）に接続できません: {ex.Message}");
        }
    }
}
