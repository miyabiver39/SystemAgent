using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SystemAgent.Core.CapabilityProviders;
using SystemAgent.Infrastructure.CapabilityProviders.Network;

namespace SystemAgent.Web.Controllers;

/// <summary>このノードのホストネットワーク（ADR-013 ③）。現在は参照のみ（設定変更はQA 0005の回答後）。</summary>
[ApiController]
[Authorize]
[Route("api/network")]
public class NetworkController(NetworkResolver resolver) : ControllerBase
{
    [HttpGet]
    public async Task<NetworkStatus> Get(CancellationToken cancellationToken) =>
        await (await resolver.ResolveAsync(cancellationToken)).GetStatusAsync(cancellationToken);
}
