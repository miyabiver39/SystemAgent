using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SystemAgent.Core.Auditing;
using SystemAgent.Core.Nodes;
using SystemAgent.Infrastructure.Cluster;
using SystemAgent.Web.Auth;

namespace SystemAgent.Web.Controllers;

/// <summary>
/// 登録済みノード。登録はクラスタ参加（/api/cluster）で行う。削除しても発行済み証明書は失効しない（ADR-012）。
/// </summary>
[ApiController]
[Authorize]
[Route("api/nodes")]
public class NodesController(INodeService nodes, ClusterIdentity identity, ClusterService cluster, IAuditLogger audit) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<NodeInfo>> List(CancellationToken cancellationToken) => nodes.ListAsync(cancellationToken);

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<NodeInfo>> Get(Guid id, CancellationToken cancellationToken) =>
        await nodes.GetAsync(id, cancellationToken) is { } node ? node : NotFound();

    /// <summary>
    /// 登録を削除する。誤操作でクラスタが機能しなくなるのを防ぐため、操作中のこのノード自身とクラスタCAのノードは
    /// force=true を指定しない限り削除しない（409）。
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, [FromQuery] bool force = false, CancellationToken cancellationToken = default)
    {
        if (!force)
        {
            if (identity.Current?.NodeId == id)
                return Problem(statusCode: StatusCodes.Status409Conflict,
                    detail: "操作中のこのノード自身の登録は削除できません（他ノードからの転送先として使えなくなります）。削除する場合は強制指定してください。");
            if (await cluster.GetCaNodeIdAsync(cancellationToken) == id)
                return Problem(statusCode: StatusCodes.Status409Conflict,
                    detail: "クラスタCAのノードの登録は削除できません（新しいノードを参加させられなくなります）。削除する場合は強制指定してください。");
        }
        if (!await nodes.DeleteAsync(id, cancellationToken)) return NotFound();

        await audit.LogAsync(User.ActorName(), "node.delete", force ? $"{id}（強制）" : id.ToString(), cancellationToken);
        return NoContent();
    }
}
