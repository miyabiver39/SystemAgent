using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using SystemAgent.Core.Auditing;
using SystemAgent.Core.Contracts;
using SystemAgent.Infrastructure.Ha;

namespace SystemAgent.Web.Controllers;

/// <summary>Keepalived + DBの昇格・降格（基本設計書 4.2節、ADR-022）。</summary>
[ApiController]
[Authorize]
[Route("api/ha")]
public class HaController(HaService ha, IAuditLogger audit) : ControllerBase
{
    public const string HookTokenHeader = "X-SystemAgent-Hook-Token";

    [HttpGet]
    public async Task<HaStatusResponse> Get(CancellationToken cancellationToken)
    {
        var state = ha.State;
        return new HaStatusResponse(View(ha.Settings), state.State, state.Since, state.RejoinPending,
            await ha.GetDbStatusAsync(cancellationToken), state.History.Reverse().ToList());
    }

    [HttpPut]
    public async Task<IActionResult> Save(SetHaSettingsRequest request, CancellationToken cancellationToken)
    {
        var localDb = string.IsNullOrEmpty(request.LocalDbPassword)
            ? null
            : new MySqlConnectionStringBuilder
            {
                Server = request.LocalDbHost ?? "127.0.0.1",
                Port = (uint)(request.LocalDbPort > 0 ? request.LocalDbPort : 3306),
                UserID = request.LocalDbUser ?? "root",
                Password = request.LocalDbPassword,
                ConnectionTimeout = 5,
            }.ConnectionString;

        ha.Save(new HaSettings(
            request.Enabled, request.Interface.Trim(), request.VirtualIp.Trim(), request.VirtualRouterId, request.Priority,
            request.AuthPass, request.UnicastPeers?.Select(p => p.Trim()).Where(p => p.Length > 0).ToList() ?? [],
            string.IsNullOrWhiteSpace(request.UnicastSourceIp) ? null : request.UnicastSourceIp.Trim(),
            request.ReturnMode, localDb, request.ReplicationUser, request.ReplicationPassword,
            request.ReplicationSourcePort > 0 ? request.ReplicationSourcePort : 3306,
            string.IsNullOrWhiteSpace(request.ReplicationSourceHost) ? null : request.ReplicationSourceHost.Trim()));
        await audit.LogAsync(User.Identity!.Name!, "ha.settings.save", $"VIP {request.VirtualIp} VRID {request.VirtualRouterId} priority {request.Priority}", cancellationToken);

        if (request.Apply) await ApplyCoreAsync(cancellationToken);
        return NoContent();
    }

    [HttpPost("apply")]
    public async Task<IActionResult> Apply(CancellationToken cancellationToken)
    {
        await ApplyCoreAsync(cancellationToken);
        return NoContent();
    }

    /// <summary>手動復帰モードで、このノードをレプリカとして再参加させる（管理者の承認）。</summary>
    [HttpPost("rejoin")]
    public async Task<IActionResult> Rejoin(CancellationToken cancellationToken)
    {
        await ha.ApproveRejoinAsync(cancellationToken);
        await audit.LogAsync(User.Identity!.Name!, "ha.rejoin", null, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// keepalived の notify（`systemagent ha notify &lt;状態&gt;`）からの通知。
    /// このノード内（ループバック）からのみ、root専用ファイルにあるフック用トークン付きで受け付ける。
    /// </summary>
    [AllowAnonymous]
    [HttpPost("notify")]
    public async Task<IActionResult> Notify(HaNotifyRequest request, CancellationToken cancellationToken)
    {
        if (HttpContext.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote)
            || !ha.VerifyHookToken(Request.Headers[HookTokenHeader]))
        {
            return Problem(statusCode: StatusCodes.Status403Forbidden, detail: "このノード内のkeepalivedからのみ通知できます。");
        }
        await ha.NotifyAsync(request.State, cancellationToken);
        return NoContent();
    }

    private async Task ApplyCoreAsync(CancellationToken cancellationToken)
    {
        await ha.ApplyAsync(cancellationToken);
        await audit.LogAsync(User.Identity!.Name!, "ha.apply", null, cancellationToken);
    }

    private static HaSettingsView? View(HaSettings? s) => s is null ? null : new HaSettingsView(
        s.Enabled, s.Interface, s.VirtualIp, s.VirtualRouterId, s.Priority, !string.IsNullOrEmpty(s.AuthPass),
        s.UnicastPeers, s.UnicastSourceIp, s.ReturnMode,
        s.LocalDb is { } db ? Describe(db) : null, s.ReplicationUser, !string.IsNullOrEmpty(s.ReplicationPassword),
        s.SourceHost, s.ReplicationSourcePort);

    private static string Describe(string connectionString)
    {
        var c = new MySqlConnectionStringBuilder(connectionString);
        return $"{c.UserID}@{c.Server}:{c.Port}";
    }
}
