using SystemAgent.Core.CapabilityProviders;
using SystemAgent.Infrastructure.CapabilityProviders.Templates;

namespace SystemAgent.Infrastructure.CapabilityProviders.Containers;

/// <summary>Container:Runtime = auto|podman|docker。autoはPodman優先（ADR-017）。</summary>
public sealed class ContainerRuntimeResolver(CapabilityTemplateResolver resolver)
{
    public async Task<IContainerRuntimeProvider> ResolveAsync(CancellationToken cancellationToken = default)
    {
        var (executor, tool) = await resolver.ResolveAsync(
            "container-runtime", "Container:Runtime", ["podman", "docker"], "コンテナランタイム", cancellationToken);
        return new TemplateContainerRuntimeProvider(executor, new RuntimeInfo(tool.Name, tool.Version, executor.Template.Id));
    }
}
