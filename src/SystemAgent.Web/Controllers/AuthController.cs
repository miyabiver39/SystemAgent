using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SystemAgent.Core.Auditing;
using SystemAgent.Core.Security;
using SystemAgent.Core.Users;
using SystemAgent.Web.Auth;

namespace SystemAgent.Web.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController(
    IUserService users,
    ILocalSecretStore secrets,
    IAuditLogger audit,
    JwtTokenIssuer issuer) : ControllerBase
{
    /// <summary>通常ログイン（DB管理ユーザー）。</summary>
    /// <remarks>DB停止中は503（ApiExceptionHandler）。</remarks>
    [HttpPost("login")]
    public async Task<ActionResult<TokenResponse>> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        if (!await users.VerifyPasswordAsync(request.UserName, request.Password, cancellationToken)) return Unauthorized();

        await audit.LogAsync(request.UserName, "auth.login", null, cancellationToken);
        return issuer.Issue(request.UserName, AuthSources.Database);
    }

    /// <summary>ローカル緊急ログイン（DB非依存）。</summary>
    [HttpPost("emergency-login")]
    public async Task<ActionResult<TokenResponse>> EmergencyLogin(LoginRequest request, CancellationToken cancellationToken)
    {
        if (!secrets.VerifyEmergencyUser(request.UserName, request.Password)) return Unauthorized();

        await audit.LogAsync(request.UserName, "auth.emergency-login", null, cancellationToken);
        return issuer.Issue(request.UserName, AuthSources.Emergency);
    }

    [Authorize]
    [HttpGet("me")]
    public ActionResult<MeResponse> Me() =>
        new MeResponse(User.Identity!.Name!, User.FindFirst(AuthConstants.AuthSourceClaim)!.Value);

    [Authorize]
    [HttpPut("emergency-users/{userName}/password")]
    public async Task<IActionResult> ChangeEmergencyPassword(
        string userName, ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        if (!secrets.ChangeEmergencyPassword(userName, request.NewPassword)) return NotFound();

        await audit.LogAsync(User.Identity!.Name!, "secret.emergency-password.change", userName, cancellationToken);
        return NoContent();
    }
}

public sealed record LoginRequest([Required] string UserName, [Required] string Password);

public sealed record ChangePasswordRequest([Required, MinLength(12)] string NewPassword);

public sealed record MeResponse(string UserName, string AuthSource);
