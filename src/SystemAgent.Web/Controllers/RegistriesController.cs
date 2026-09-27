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
public class RegistriesController(
    ContainerRuntimeResolver resolver, RegistryService registries, RegistryBrowser browser, IAuditLogger audit) : ControllerBase
{
    [HttpGet]
    public IReadOnlyList<RegistryView> List() => registries.List();

    /// <summary>ログインを試し、成功した場合だけ保存する。</summary>
    [HttpPut]
    public async Task<IActionResult> Save(SaveRegistryRequest request, CancellationToken cancellationToken)
    {
        var runtime = await resolver.ResolveAsync(cancellationToken);
        var registry = await registries.SaveAsync(runtime, request.Registry, request.Username, request.Password, request.TlsVerify, cancellationToken);
        await audit.LogAsync(User.Identity!.Name!, "registry.login", $"{runtime.Runtime.Name}: {request.Username}@{registry}", cancellationToken);
        return NoContent();
    }

    /// <summary>レジストリ内のリポジトリ一覧（ADR-026）。</summary>
    [HttpGet("{registry}/repositories")]
    public Task<IReadOnlyList<string>> Repositories(string registry, CancellationToken cancellationToken) =>
        browser.ListRepositoriesAsync(registry, cancellationToken);

    /// <summary>リポジトリのタグ一覧。リポジトリ名は "/" を含むためクエリで渡す。</summary>
    [HttpGet("{registry}/tags")]
    public async Task<RegistryTagsResponse> Tags(string registry, [FromQuery] string repository, CancellationToken cancellationToken) =>
        new(repository, await browser.ListTagsAsync(registry, repository, cancellationToken));

    [HttpDelete("{registry}/tags")]
    public async Task<IActionResult> DeleteTag(string registry, [FromQuery] string repository, [FromQuery] string tag, CancellationToken cancellationToken)
    {
        await browser.DeleteTagAsync(registry, repository, tag, cancellationToken);
        await audit.LogAsync(User.Identity!.Name!, "registry.tag.delete", $"{registry}/{repository}:{tag}", cancellationToken);
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
