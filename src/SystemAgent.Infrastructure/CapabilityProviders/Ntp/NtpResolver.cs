using Microsoft.Extensions.Logging;
using SystemAgent.Core.CapabilityProviders;
using SystemAgent.Infrastructure.CapabilityProviders.Templates;

namespace SystemAgent.Infrastructure.CapabilityProviders.Ntp;

/// <summary>Ntp:Implementation = auto|chronyc|timesyncd。autoはchrony優先。</summary>
public sealed class NtpResolver(CapabilityTemplateResolver resolver, ILogger<TemplateNtpProvider> logger)
{
    public async Task<INtpProvider> ResolveAsync(CancellationToken cancellationToken = default)
    {
        var (executor, _) = await resolver.ResolveAsync(
            "ntp", "Ntp:Implementation", ["chronyc", "timesyncd"], "時刻同期サービス", cancellationToken);
        return new TemplateNtpProvider(executor, logger);
    }
}
