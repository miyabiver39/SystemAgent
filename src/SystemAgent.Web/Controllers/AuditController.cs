using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SystemAgent.Core.Auditing;
using SystemAgent.Core.Contracts;
using SystemAgent.Infrastructure.Auditing;
using SystemAgent.Web.Auth;

namespace SystemAgent.Web.Controllers;

/// <summary>監査ログの閲覧（ADR-025）。中央DBの記録を返すため、ノードの転送は不要。</summary>
[ApiController]
[Authorize]
[Route("api/audit")]
public class AuditController(AuditLogReader reader, IAuditLogger audit) : ControllerBase
{
    [HttpGet]
    public Task<AuditLogPage> Search([FromQuery] AuditLogQuery query, CancellationToken cancellationToken) =>
        reader.SearchAsync(query, cancellationToken);

    [HttpGet("facets")]
    public Task<AuditLogFacets> Facets(CancellationToken cancellationToken) => reader.FacetsAsync(cancellationToken);

    /// <summary>CSV出力。持ち出しも監査ログに残す。</summary>
    [HttpGet("export")]
    public async Task Export([FromQuery] AuditLogQuery query, CancellationToken cancellationToken)
    {
        await audit.LogAsync(User.ActorName(), "audit.export", Describe(query), cancellationToken);
        Response.ContentType = "text/csv; charset=utf-8";
        Response.Headers.ContentDisposition = $"attachment; filename=\"audit-{DateTime.Now:yyyyMMdd-HHmmss}.csv\"";
        await reader.ExportCsvAsync(query, Response.Body, cancellationToken);
    }

    private static string Describe(AuditLogQuery q)
    {
        var conditions = new[]
        {
            q.From is null ? null : $"from={q.From:o}", q.To is null ? null : $"to={q.To:o}",
            string.IsNullOrWhiteSpace(q.Actor) ? null : $"actor={q.Actor}", string.IsNullOrWhiteSpace(q.Action) ? null : $"action={q.Action}",
            string.IsNullOrWhiteSpace(q.Node) ? null : $"node={q.Node}", string.IsNullOrWhiteSpace(q.Text) ? null : $"text={q.Text}",
        }.OfType<string>().ToList();
        return conditions.Count == 0 ? "全件" : string.Join(" ", conditions);
    }
}
