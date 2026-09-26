using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SystemAgent.Core.Auditing;
using SystemAgent.Core.Contracts;
using SystemAgent.Infrastructure.Cluster;

namespace SystemAgent.Web.Controllers;

/// <summary>クラスタ（自己CA・ノード参加）。基本設計書 4.1節、ADR-018。</summary>
[ApiController]
[Authorize]
[Route("api/cluster")]
public class ClusterController(ClusterService cluster, ClusterEndpointSettings endpoint, IAuditLogger audit) : ControllerBase
{
    [HttpGet]
    public ClusterStatusResponse Status() => cluster.GetStatus();

    /// <summary>このノードをクラスタCAとして初期化する（クラスタで1回だけ）。</summary>
    [HttpPost("init")]
    public async Task<ClusterStatusResponse> Initialize(InitializeClusterRequest request, CancellationToken cancellationToken)
    {
        var status = await cluster.InitializeAsync(request.ClusterName, cancellationToken);
        await audit.LogAsync(User.Identity!.Name!, "cluster.init", $"{request.ClusterName} ({status.NodeId})", cancellationToken);
        return status;
    }

    /// <summary>ノード参加用のワンタイムトークンを発行する。</summary>
    [HttpPost("tokens")]
    public async Task<JoinTokenResponse> CreateToken(CreateJoinTokenRequest request, CancellationToken cancellationToken)
    {
        var token = await cluster.CreateJoinTokenAsync(User.Identity!.Name!, request.ValidMinutes, cancellationToken);
        await audit.LogAsync(User.Identity!.Name!, "cluster.token.create", $"expires {token.ExpiresAt:O}", cancellationToken);
        return token;
    }

    /// <summary>このノードを、参加トークンが示すクラスタに参加させる。</summary>
    [HttpPost("join")]
    public async Task<ClusterStatusResponse> Join(JoinClusterRequest request, CancellationToken cancellationToken)
    {
        var status = await cluster.JoinAsync(request.Token, cancellationToken);
        await audit.LogAsync(User.Identity!.Name!, "cluster.join", $"{status.ClusterName} ({status.NodeId})", cancellationToken);
        return status;
    }

    /// <summary>CA証明書（公開情報）。参加前のノードが、トークンの指紋と照合するために取得する。</summary>
    [AllowAnonymous]
    [HttpGet("ca")]
    public async Task<IActionResult> CaCertificate(CancellationToken cancellationToken) =>
        await cluster.GetCaCertificatePemAsync(cancellationToken) is { } pem ? Content(pem, "application/x-pem-file") : NotFound();

    /// <summary>
    /// CAノード: 新規ノードの証明書発行と登録。参加トークンで認可する。
    /// TLS（ノード間通信ポート）上でのみ受け付ける（トークンと証明書を平文で流さないため）。
    /// </summary>
    [AllowAnonymous]
    [HttpPost("enroll")]
    public async Task<ActionResult<EnrollResponse>> Enroll(EnrollRequest request, CancellationToken cancellationToken)
    {
        if (HttpContext.Connection.LocalPort != endpoint.Port || !Request.IsHttps) return NotFound();

        var enrolled = await cluster.EnrollAsync(request, cancellationToken);
        await audit.LogAsync($"node:{request.NodeName}", "cluster.enroll",
            $"{request.NodeName} {request.Address}:{request.ClusterPort} ({enrolled.NodeId})", cancellationToken);
        return enrolled;
    }
}
