using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using SystemAgent.Infrastructure.Cluster;

namespace SystemAgent.Web.Auth;

/// <summary>
/// ノード間通信（mTLSポート）の認証。クライアント証明書がクラスタCAで署名されたノード証明書なら認証する。
/// 転送元ノードが付けた操作者（X-SystemAgent-Actor）を利用者名として扱い、監査ログに残す。
/// DBには依存しない（CA証明書はローカル秘密情報にある）。
/// </summary>
public sealed partial class NodeCertificateAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    ClusterIdentity identity, ClusterEndpointSettings endpoint)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "NodeCertificate";
    public const string NodeIdClaim = "node_id";

    /// <summary>
    /// 転送元が付ける操作者名（ユーザー名@転送元ノード）として受け付ける形式。ユーザー名の規則（UserNamePolicy）とホスト名の文字に限る。
    /// 改行・制御文字・CSVやHTMLで意味を持つ文字で監査ログを偽装・破損させないため。
    /// </summary>
    public static bool IsValidActor(string actor) => ActorPattern().IsMatch(actor);

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}(@[A-Za-z0-9._-]{1,253})?$")]
    private static partial Regex ActorPattern();

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Context.Connection.LocalPort != endpoint.Port) return AuthenticateResult.NoResult();

        var certificate = await Context.Connection.GetClientCertificateAsync();
        if (certificate is null) return AuthenticateResult.NoResult();

        if (identity.Current is not { } current) return AuthenticateResult.Fail("このノードはクラスタに参加していません。");
        if (Pki.ValidateNodeCertificate(certificate, current.CaCertificate, NodeCertificateUsage.Client) is not { } nodeId)
            return AuthenticateResult.Fail("クラスタCAで署名されたノード証明書ではありません。");

        var actor = $"node:{nodeId}";
        if (Request.Headers[ClusterHttpClientFactory.ActorHeader].FirstOrDefault() is { Length: > 0 } header)
        {
            if (IsValidActor(header)) actor = header;
            else Logger.LogWarning("ノード {NodeId} から不正な操作者名を受け取ったため、ノードの操作として記録します。", nodeId);
        }
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(AuthConstants.NameClaim, actor),
            new Claim(AuthConstants.AuthSourceClaim, "node"),
            new Claim(NodeIdClaim, nodeId.ToString()),
        ], SchemeName, AuthConstants.NameClaim, null));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }
}
