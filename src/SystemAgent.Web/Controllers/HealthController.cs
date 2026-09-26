using Microsoft.AspNetCore.Mvc;
using SystemAgent.Core.Health;

namespace SystemAgent.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public class HealthController(IDatabaseStatus database, TimeProvider time) : ControllerBase
{
    /// <summary>認証不要。databaseがfalseの場合、WebUI/CLIは緊急ログインへ誘導する。</summary>
    [HttpGet]
    public async Task<HealthResponse> Get(CancellationToken cancellationToken) =>
        new("ok", await database.CanConnectAsync(cancellationToken), time.GetUtcNow());
}

public sealed record HealthResponse(string Status, bool Database, DateTimeOffset TimestampUtc);
