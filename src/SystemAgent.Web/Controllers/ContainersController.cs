using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SystemAgent.Core.Auditing;
using SystemAgent.Core.CapabilityProviders;
using SystemAgent.Core.Contracts;
using SystemAgent.Infrastructure.CapabilityProviders.Containers;

namespace SystemAgent.Web.Controllers;

/// <summary>このノードのコンテナ管理（ADR-013 ②）。ランタイムがない環境では501。</summary>
[ApiController]
[Authorize]
[Route("api/containers")]
public class ContainersController(ContainerRuntimeResolver resolver, IAuditLogger audit) : ControllerBase
{
    [HttpGet("runtime")]
    public async Task<ContainerRuntimeResponse> Runtime(CancellationToken cancellationToken)
    {
        var runtime = await resolver.ResolveAsync(cancellationToken);
        return new ContainerRuntimeResponse(runtime.Runtime.Name, runtime.Runtime.Version, runtime.Runtime.TemplateId, runtime.SupportsPods);
    }

    [HttpGet]
    public async Task<IReadOnlyList<ContainerInfo>> List(CancellationToken cancellationToken) =>
        await (await resolver.ResolveAsync(cancellationToken)).ListContainersAsync(cancellationToken);

    [HttpPost("{id}/start")]
    public Task<IActionResult> Start(string id, CancellationToken cancellationToken) =>
        ExecuteAsync("container.start", id, (r, ct) => r.StartContainerAsync(id, ct), cancellationToken);

    [HttpPost("{id}/stop")]
    public Task<IActionResult> Stop(string id, CancellationToken cancellationToken) =>
        ExecuteAsync("container.stop", id, (r, ct) => r.StopContainerAsync(id, ct), cancellationToken);

    [HttpPost("{id}/restart")]
    public Task<IActionResult> Restart(string id, CancellationToken cancellationToken) =>
        ExecuteAsync("container.restart", id, (r, ct) => r.RestartContainerAsync(id, ct), cancellationToken);

    [HttpDelete("{id}")]
    public Task<IActionResult> Remove(string id, CancellationToken cancellationToken) =>
        ExecuteAsync("container.remove", id, (r, ct) => r.RemoveContainerAsync(id, ct), cancellationToken);

    [HttpGet("{id}/logs")]
    public async Task<ContainerLogsResponse> Logs(string id, [FromQuery] int tail = 200, CancellationToken cancellationToken = default) =>
        new(await (await resolver.ResolveAsync(cancellationToken)).GetContainerLogsAsync(id, tail, cancellationToken));

    [HttpGet("/api/pods")]
    public async Task<IReadOnlyList<PodInfo>> Pods(CancellationToken cancellationToken) =>
        await (await resolver.ResolveAsync(cancellationToken)).ListPodsAsync(cancellationToken);

    private async Task<IActionResult> ExecuteAsync(
        string action, string id, Func<IContainerRuntimeProvider, CancellationToken, Task> operation, CancellationToken cancellationToken)
    {
        var runtime = await resolver.ResolveAsync(cancellationToken);
        await operation(runtime, cancellationToken);
        await audit.LogAsync(User.Identity!.Name!, action, $"{runtime.Runtime.Name}: {id}", cancellationToken);
        return NoContent();
    }
}
