using SystemAgent.Core.CapabilityProviders;
using SystemAgent.Infrastructure.CapabilityProviders.Templates;

namespace SystemAgent.Infrastructure.CapabilityProviders.Network;

public sealed class NetworkResolver(CapabilityTemplateResolver resolver)
{
    public async Task<INetworkProvider> ResolveAsync(CancellationToken cancellationToken = default)
    {
        var (executor, _) = await resolver.ResolveAsync("network", "Network:Tool", ["ip"], "ネットワーク情報の取得ツール", cancellationToken);
        return new TemplateNetworkProvider(executor);
    }
}
