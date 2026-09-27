using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SystemAgent.Core.Auditing;
using SystemAgent.Core.CapabilityProviders;
using SystemAgent.Core.Contracts;
using SystemAgent.Infrastructure.CapabilityProviders.Services;
using SystemAgent.Web.Auth;

namespace SystemAgent.Web.Controllers;

/// <summary>このノードのサービス（systemd）。ADR-021。</summary>
[ApiController]
[Authorize]
[Route("api/services")]
public class ServicesController(ServiceManagement management, IAuditLogger audit) : ControllerBase
{
    /// <summary>管理対象サービスのうち、このノードに存在するもの。</summary>
    [HttpGet]
    public async Task<IReadOnlyList<ManagedServiceResponse>> List(CancellationToken cancellationToken)
    {
        var provider = await management.ResolveAsync(cancellationToken);
        var result = new List<ManagedServiceResponse>();
        foreach (var unit in management.Managed)
        {
            var status = await provider.GetStatusAsync(unit, cancellationToken);
            // 別名（例: mysqld → mariadb）で同じサービスが重複しないようにする
            if (status.Exists && result.All(r => r.Status.Name != status.Name)) result.Add(new ManagedServiceResponse(status, true));
        }
        return result;
    }

    [HttpGet("{unit}")]
    public async Task<ActionResult<ManagedServiceResponse>> Get(string unit, CancellationToken cancellationToken)
    {
        var status = await (await management.ResolveAsync(cancellationToken)).GetStatusAsync(unit, cancellationToken);
        if (!status.Exists) return NotFound();
        return new ManagedServiceResponse(status, management.Deny(unit, ServiceAction.Restart) is null);
    }

    // "action" はMVCの予約ルート値のため operation とする
    [HttpPost("{unit}/{operation}")]
    public async Task<IActionResult> Execute(string unit, ServiceAction operation, CancellationToken cancellationToken)
    {
        if (management.Deny(unit, operation) is { } reason) return Problem(statusCode: StatusCodes.Status403Forbidden, detail: reason);

        await (await management.ResolveAsync(cancellationToken)).ExecuteAsync(unit, operation, cancellationToken);
        await audit.LogAsync(User.ActorName(), $"service.{operation.ToString().ToLowerInvariant()}", unit, cancellationToken);
        return NoContent();
    }

    [HttpGet("{unit}/logs")]
    public async Task<ServiceLogsResponse> Logs(string unit, [FromQuery] int lines = 200, CancellationToken cancellationToken = default) =>
        new(await (await management.ResolveAsync(cancellationToken)).GetLogsAsync(unit, lines, cancellationToken));
}
