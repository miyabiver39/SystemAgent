using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using SystemAgent.Infrastructure.Cluster;

namespace SystemAgent.Web.Auth;

/// <summary>
/// ノード間通信（mTLSポート）の認証。クライアント証明書がクラスタCAで署名されたノード証明書なら認証する。
/// 転送元ノードが付けた操作者（X-SystemAgent-Actor）を利用者名として扱い、監査ログに残す。
/// DBには依存しない（CA証明書はローカル秘密情報にある）。
/// </summary>
public sealed class NodeCertificateAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    ClusterIdentity identity, ClusterEndpointSettings endpoint)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "NodeCertificate";
    public const string NodeIdClaim = "node_id";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Context.Connection.LocalPort != endpoint.Port) return AuthenticateResult.NoResult();

        var certificate = await Context.Connection.GetClientCertificateAsync();
        if (certificate is null) return AuthenticateResult.NoResult();

        if (identity.Current is not { } current) return AuthenticateResult.Fail("このノードはクラスタに参加していません。");
        if (Pki.ValidateNodeCertificate(certificate, current.CaCertificate, NodeCertificateUsage.Client) is not { } nodeId)
            return AuthenticateResult.Fail("クラスタCAで署名されたノード証明書ではありません。");

        var actor = Request.Headers[ClusterHttpClientFactory.ActorHeader].FirstOrDefault() is { Length: > 0 and <= 200 } header
            ? header
            : $"node:{nodeId}";
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(AuthConstants.NameClaim, actor),
            new Claim(AuthConstants.AuthSourceClaim, "node"),
            new Claim(NodeIdClaim, nodeId.ToString()),
        ], SchemeName, AuthConstants.NameClaim, null));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }
}
