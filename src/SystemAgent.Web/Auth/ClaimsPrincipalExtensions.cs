using System.Security.Claims;

namespace SystemAgent.Web.Auth;

public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// 監査ログに残す操作者名。ユーザー名、または他ノード経由の操作なら「ユーザー名@転送元ノード」（ADR-018）。
    /// 認証済みのAPIでのみ使う（未認証なら例外）。
    /// </summary>
    public static string ActorName(this ClaimsPrincipal user) =>
        user.Identity?.Name ?? throw new InvalidOperationException("認証されていない要求で操作者名を参照しました。");
}
