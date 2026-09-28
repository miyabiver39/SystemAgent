using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SystemAgent.Core.Auditing;
using SystemAgent.Core.Contracts;
using SystemAgent.Core.Security;
using SystemAgent.Core.Users;
using SystemAgent.Web.Auth;

namespace SystemAgent.Web.Controllers;

/// <summary>
/// ログイン。総当たり対策として、接続元ごとの試行回数を制限し（レート制限 "auth"）、アカウントごとに連続失敗でロックアウトする
/// （LoginThrottle）。失敗も監査ログに残す。
/// </summary>
[ApiController]
[Route("api/auth")]
public partial class AuthController(
    IUserService users,
    ILocalSecretStore secrets,
    IAuditLogger audit,
    JwtTokenIssuer issuer,
    LoginThrottle throttle) : ControllerBase
{
    /// <summary>通常ログイン（DB管理ユーザー）。</summary>
    /// <remarks>DB停止中は503（ApiExceptionHandler）。</remarks>
    [HttpPost("login")]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public Task<ActionResult<TokenResponse>> Login(LoginRequest request, CancellationToken cancellationToken) =>
        LoginAsync(request, AuthSources.Database, "auth.login",
            () => users.VerifyPasswordAsync(request.UserName, request.Password, cancellationToken), cancellationToken);

    /// <summary>ローカル緊急ログイン（DB非依存）。</summary>
    [HttpPost("emergency-login")]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public Task<ActionResult<TokenResponse>> EmergencyLogin(LoginRequest request, CancellationToken cancellationToken) =>
        LoginAsync(request, AuthSources.Emergency, "auth.emergency-login",
            () => Task.FromResult(secrets.VerifyEmergencyUser(request.UserName, request.Password)), cancellationToken);

    private async Task<ActionResult<TokenResponse>> LoginAsync(LoginRequest request, string authSource, string action,
        Func<Task<bool>> verify, CancellationToken cancellationToken)
    {
        var key = $"{authSource}:{request.UserName}";
        var actor = AuditActor(request.UserName);
        if (throttle.LockedFor(key) is { } remaining)
        {
            await audit.LogAsync(actor, action + ".failed", $"ロックアウト中 from {ClientAddress}", cancellationToken);
            Response.Headers.RetryAfter = ((int)Math.Ceiling(remaining.TotalSeconds)).ToString();
            return Problem(statusCode: StatusCodes.Status429TooManyRequests,
                detail: $"ログインの失敗が続いたため、このアカウントは一時的にロックされています。約{Math.Ceiling(remaining.TotalMinutes)}分後にやり直してください。");
        }

        if (!await verify())
        {
            var lockedOut = throttle.RecordFailure(key);
            await audit.LogAsync(actor, action + ".failed",
                $"パスワード不一致または無効なユーザー from {ClientAddress}" + (lockedOut ? "（連続失敗のためロックアウト）" : ""), cancellationToken);
            return Unauthorized();
        }

        throttle.RecordSuccess(key);
        await audit.LogAsync(request.UserName, action, $"from {ClientAddress}", cancellationToken);
        return issuer.Issue(request.UserName, authSource);
    }

    /// <summary>監査ログの接続元。WebUIからのログインはこのノード自身（ループバック）からの要求になる。</summary>
    private string ClientAddress => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "不明";

    /// <summary>失敗時は利用者の入力をそのまま実行者にするため、ユーザー名の形式でなければ置き換える（監査ログの偽装・破損防止）。</summary>
    public static string AuditActor(string userName) => ValidUserName().IsMatch(userName) ? userName : "(不正なユーザー名)";

    [GeneratedRegex(UserNamePolicy.Pattern)]
    private static partial Regex ValidUserName();

    [Authorize]
    [HttpGet("me")]
    public ActionResult<MeResponse> Me() =>
        new MeResponse(User.ActorName(), User.FindFirst(AuthConstants.AuthSourceClaim)!.Value);

    /// <summary>緊急認証ユーザーのパスワード変更。そのユーザーの現在のパスワードで本人確認する（通常ログイン中の利用者でも同じ）。</summary>
    [Authorize]
    [HttpPut("emergency-users/{userName}/password")]
    public async Task<IActionResult> ChangeEmergencyPassword(
        string userName, ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        if (!secrets.VerifyEmergencyUser(userName, request.CurrentPassword))
        {
            await audit.LogAsync(User.ActorName(), "secret.emergency-password.change.failed", $"{userName}: 現在のパスワードが一致しません", cancellationToken);
            return Problem(statusCode: StatusCodes.Status400BadRequest, detail: UsersController.CurrentPasswordMismatch);
        }
        if (PasswordPolicy.Validate(userName, request.NewPassword, request.CurrentPassword) is { } error)
            return Problem(statusCode: StatusCodes.Status400BadRequest, detail: error);
        if (!secrets.ChangeEmergencyPassword(userName, request.NewPassword)) return NotFound();

        await audit.LogAsync(User.ActorName(), "secret.emergency-password.change", userName, cancellationToken);
        return NoContent();
    }
}
