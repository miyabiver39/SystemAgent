using Microsoft.AspNetCore.Mvc;

namespace SystemAgent.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class HealthController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new { status = "ok", timestampUtc = DateTimeOffset.UtcNow });
}
