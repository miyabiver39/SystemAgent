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

    [HttpPost]
    public async Task<ActionResult<UserSummary>> Create(CreateUserRequest request, CancellationToken cancellationToken)
    {
        var created = await users.CreateAsync(request.UserName, request.Password, cancellationToken);
        if (created is null) return Conflict();

        await audit.LogAsync(User.ActorName(), "user.create", request.UserName, cancellationToken);
        return Created($"/api/users/{created.UserName}", created);
    }

    [HttpPut("{userName}/password")]
    public async Task<IActionResult> ChangePassword(string userName, ChangePasswordRequest request, CancellationToken cancellationToken)
    {
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
