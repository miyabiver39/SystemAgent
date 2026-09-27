using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SystemAgent.Core.Auditing;
using SystemAgent.Core.Contracts;
using SystemAgent.Core.Users;
using SystemAgent.Web.Auth;

namespace SystemAgent.Web.Controllers;

[ApiController]
[Authorize]
[Route("api/users")]
public class UsersController(IUserService users, IAuditLogger audit) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<UserSummary>> List(CancellationToken cancellationToken) => users.ListAsync(cancellationToken);

    public const string CurrentPasswordMismatch = "現在のパスワードが正しくありません。";

    [HttpPost]
    public async Task<ActionResult<UserSummary>> Create(CreateUserRequest request, CancellationToken cancellationToken)
    {
        if (PasswordPolicy.Validate(request.UserName, request.Password) is { } error)
            return Problem(statusCode: StatusCodes.Status400BadRequest, detail: error);
        var created = await users.CreateAsync(request.UserName, request.Password, cancellationToken);
        if (created is null) return Conflict();

        await audit.LogAsync(User.ActorName(), "user.create", request.UserName, cancellationToken);
        return Created($"/api/users/{created.UserName}", created);
    }

    /// <summary>パスワード変更。対象ユーザーの現在のパスワードで本人確認する（誤りは400。ユーザーの有無は区別しない）。</summary>
    [HttpPut("{userName}/password")]
    public async Task<IActionResult> ChangePassword(string userName, ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        if (!await users.VerifyPasswordAsync(userName, request.CurrentPassword, cancellationToken))
        {
            await audit.LogAsync(User.ActorName(), "user.password.change.failed", $"{userName}: 現在のパスワードが一致しません", cancellationToken);
            return Problem(statusCode: StatusCodes.Status400BadRequest, detail: CurrentPasswordMismatch);
        }
        if (PasswordPolicy.Validate(userName, request.NewPassword, request.CurrentPassword) is { } error)
            return Problem(statusCode: StatusCodes.Status400BadRequest, detail: error);
        if (!await users.ChangePasswordAsync(userName, request.NewPassword, cancellationToken)) return NotFound();

        await audit.LogAsync(User.ActorName(), "user.password.change", userName, cancellationToken);
        return NoContent();
    }

    [HttpDelete("{userName}")]
    public async Task<IActionResult> Delete(string userName, CancellationToken cancellationToken)
    {
        if (!await users.DeleteAsync(userName, cancellationToken)) return NotFound();

        await audit.LogAsync(User.ActorName(), "user.delete", userName, cancellationToken);
        return NoContent();
    }
}
