using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SystemAgent.Core.Auditing;
using SystemAgent.Core.Nodes;

namespace SystemAgent.Web.Controllers;

/// <summary>
/// 登録済みノード。登録はクラスタ参加（/api/cluster）で行う。削除しても発行済み証明書は失効しない（ADR-012）。
/// </summary>
[ApiController]
[Authorize]
[Route("api/nodes")]
public class NodesController(INodeService nodes, IAuditLogger audit) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<NodeInfo>> List(CancellationToken cancellationToken) => nodes.ListAsync(cancellationToken);

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<NodeInfo>> Get(Guid id, CancellationToken cancellationToken) =>
        await nodes.GetAsync(id, cancellationToken) is { } node ? node : NotFound();

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        if (!await nodes.DeleteAsync(id, cancellationToken)) return NotFound();

        await audit.LogAsync(User.Identity!.Name!, "node.delete", id.ToString(), cancellationToken);
        return NoContent();
    }
}
