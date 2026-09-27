using Microsoft.AspNetCore.Mvc;
using SystemAgent.Core.Auditing;
using SystemAgent.Core.Contracts;
using SystemAgent.Core.Security;

namespace SystemAgent.Web.Controllers;

/// <summary>
/// 初期セットアップ（緊急認証の管理者作成）。認証不要だが、サーバー上のsetup-tokenファイルの値を要求する（ADR-015）。
/// </summary>
[ApiController]
[Route("api/setup")]
public class SetupController(ILocalSecretStore secrets, IAuditLogger audit) : ControllerBase
{
    [HttpGet]
    public SetupStatusResponse Status() => new(secrets.IsSetupRequired);

    [HttpPost]
    public async Task<IActionResult> Complete(SetupRequest request, CancellationToken cancellationToken)
    {
        if (PasswordPolicy.Validate(request.UserName, request.Password) is { } error)
            return Problem(statusCode: StatusCodes.Status400BadRequest, detail: error);
        switch (secrets.CompleteSetup(request.SetupToken, request.UserName, request.Password))
        {
            case SetupResult.AlreadyCompleted:
                return Problem(statusCode: StatusCodes.Status409Conflict, detail: "初期セットアップは完了済みです。");
            case SetupResult.InvalidToken:
                return Problem(statusCode: StatusCodes.Status403Forbidden, detail: "セットアップトークンが正しくありません。");
        }

        await audit.LogAsync(request.UserName, "setup.complete", null, cancellationToken);
        return NoContent();
    }
}
