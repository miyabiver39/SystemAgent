using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SystemAgent.Core.Auditing;
using SystemAgent.Core.Contracts;
using SystemAgent.Infrastructure.CapabilityProviders.Ntp;
using SystemAgent.Web.Auth;

namespace SystemAgent.Web.Controllers;

/// <summary>このノードの時刻同期（NTP）設定（ADR-013 ③）。</summary>
[ApiController]
[Authorize]
[Route("api/ntp")]
public class NtpController(NtpResolver resolver, IAuditLogger audit) : ControllerBase
{
    [HttpGet]
    public async Task<NtpResponse> Get(CancellationToken cancellationToken)
    {
        var ntp = await resolver.ResolveAsync(cancellationToken);
        return new NtpResponse(ntp.Implementation, await ntp.GetStatusAsync(cancellationToken));
    }

    /// <summary>参照するNTPサーバーを置き換える。サービス再起動に失敗した場合は設定を元に戻して422を返す。</summary>
    [HttpPut("servers")]
    public async Task<IActionResult> SetServers(SetNtpServersRequest request, CancellationToken cancellationToken)
    {
        var ntp = await resolver.ResolveAsync(cancellationToken);
        await ntp.SetServersAsync(request.Servers, cancellationToken);
        await audit.LogAsync(User.ActorName(), "ntp.servers.set",
            $"{ntp.Implementation.Name}: {string.Join(", ", request.Servers)}", cancellationToken);
        return NoContent();
    }

    [HttpPost("sync")]
    public async Task<IActionResult> SyncNow(CancellationToken cancellationToken)
    {
        var ntp = await resolver.ResolveAsync(cancellationToken);
        await ntp.SyncNowAsync(cancellationToken);
        await audit.LogAsync(User.ActorName(), "ntp.sync", ntp.Implementation.Name, cancellationToken);
        return NoContent();
    }
}
