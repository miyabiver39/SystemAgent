using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SystemAgent.Core.Auditing;
using SystemAgent.Core.Nodes;

namespace SystemAgent.Web.Controllers;

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

    [HttpPost]
    public async Task<ActionResult<NodeInfo>> Register(RegisterNodeRequest request, CancellationToken cancellationToken)
    {
        var node = await nodes.RegisterAsync(
            new NodeRegistration(request.HostName, request.IpAddress, request.Os, request.Role), cancellationToken);
        if (node is null) return Conflict();

        await audit.LogAsync(User.Identity!.Name!, "node.register", $"{node.HostName} ({node.Id})", cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = node.Id }, node);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        if (!await nodes.DeleteAsync(id, cancellationToken)) return NotFound();

        await audit.LogAsync(User.Identity!.Name!, "node.delete", id.ToString(), cancellationToken);
        return NoContent();
    }
}

public sealed record RegisterNodeRequest(
    [Required, MaxLength(253)] string HostName,
    [Required, MaxLength(45)] string IpAddress,
    [Required] OsInfo Os,
    NodeRole Role = NodeRole.Managed);
