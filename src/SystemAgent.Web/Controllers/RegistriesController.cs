using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SystemAgent.Core.Auditing;
using SystemAgent.Core.Contracts;
using SystemAgent.Infrastructure.CapabilityProviders.Containers;

namespace SystemAgent.Web.Controllers;

/// <summary>このノードのコンテナレジストリの認証情報（ADR-023）。</summary>
[ApiController]
[Authorize]
[Route("api/registries")]
public class RegistriesController(ContainerRuntimeResolver resolver, RegistryService registries, IAuditLogger audit) : ControllerBase
{
    [HttpGet]
    public IReadOnlyList<RegistryView> List() => registries.List();

    /// <summary>ログインを試し、成功した場合だけ保存する。</summary>
    [HttpPut]
    public async Task<IActionResult> Save(SaveRegistryRequest request, CancellationToken cancellationToken)
    {
        var runtime = await resolver.ResolveAsync(cancellationToken);
        var registry = await registries.SaveAsync(runtime, request.Registry, request.Username, request.Password, cancellationToken);
        await audit.LogAsync(User.Identity!.Name!, "registry.login", $"{runtime.Runtime.Name}: {request.Username}@{registry}", cancellationToken);
        return NoContent();
    }

    [HttpDelete("{registry}")]
    public async Task<IActionResult> Remove(string registry, CancellationToken cancellationToken)
    {
        var runtime = await resolver.ResolveAsync(cancellationToken);
        if (!await registries.RemoveAsync(runtime, registry, cancellationToken)) return NotFound();
        await audit.LogAsync(User.Identity!.Name!, "registry.logout", $"{runtime.Runtime.Name}: {registry}", cancellationToken);
        return NoContent();
    }
}
