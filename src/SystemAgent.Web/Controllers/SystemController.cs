using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SystemAgent.Core.CapabilityProviders;

namespace SystemAgent.Web.Controllers;

[ApiController]
[Authorize]
[Route("api/system")]
public class SystemController(IEnvironmentDetector detector) : ControllerBase
{
    /// <summary>このノードのOS情報と、検出された管理対象ツール（基本設計書 7章 環境検出）。</summary>
    [HttpGet("environment")]
    public Task<HostEnvironment> Environment([FromQuery] bool refresh = false, CancellationToken cancellationToken = default) =>
        detector.DetectAsync(refresh, cancellationToken);
}
