using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SystemAgent.Core.Auditing;
using SystemAgent.Core.Contracts;
using SystemAgent.Core.Deploy;
using SystemAgent.Infrastructure.Deploy;
using SystemAgent.Core.Errors;
using SystemAgent.Web.Auth;

namespace SystemAgent.Web.Controllers;

/// <summary>このノードのアプリ（コンテナ）のデプロイ。ADR-024。</summary>
[ApiController]
[Authorize]
[Route("api/deployments")]
public class DeploymentsController(DeploymentService deployments, IAuditLogger audit) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<DeploymentView>> List(CancellationToken cancellationToken) => deployments.ListAsync(cancellationToken);

    [HttpGet("{name}")]
    public async Task<ActionResult<DeploymentView>> Get(string name, CancellationToken cancellationToken) =>
        await deployments.GetAsync(name, cancellationToken) is { } view ? view : NotFound();

    /// <summary>定義を追加・更新する（デプロイはしない）。URLの名前と本文の名前は一致させる。</summary>
    [HttpPut("{name}")]
    public async Task<IActionResult> Save(string name, DeploymentSpec spec, CancellationToken cancellationToken)
    {
        if (spec.Name.Trim() != name) return Problem(statusCode: StatusCodes.Status400BadRequest, detail: "URLと本文のコンテナ名が一致しません。");
        var saved = deployments.Save(spec);
        await audit.LogAsync(User.ActorName(), "deploy.define", $"{saved.Name}: {saved.Image}", cancellationToken);
        return NoContent();
    }

    [HttpDelete("{name}")]
    public async Task<IActionResult> Remove(string name, [FromQuery] bool removeContainer = false, CancellationToken cancellationToken = default)
    {
        if (!await deployments.RemoveAsync(name, removeContainer, cancellationToken)) return NotFound();
        await audit.LogAsync(User.ActorName(), "deploy.remove", $"{name}{(removeContainer ? "（コンテナも削除）" : "")}", cancellationToken);
        return NoContent();
    }

    [HttpPost("{name}/deploy")]
    public Task<DeploymentView> Deploy(string name, DeployRequest request, CancellationToken cancellationToken) =>
        AuditedAsync("deploy.deploy", $"{name}: {request.Tag}", () => deployments.DeployAsync(name, request.Tag.Trim(), User.ActorName(), cancellationToken), cancellationToken);

    [HttpPost("{name}/rollback")]
    public Task<DeploymentView> Rollback(string name, CancellationToken cancellationToken) =>
        AuditedAsync("deploy.rollback", name, () => deployments.RollbackAsync(name, User.ActorName(), cancellationToken), cancellationToken);

    /// <summary>成功・失敗とも監査ログに残す（失敗時は元のコンテナに戻したことも含めて記録する）。</summary>
    private async Task<DeploymentView> AuditedAsync(string action, string target, Func<Task<DeploymentView>> operation, CancellationToken cancellationToken)
    {
        try
        {
            var view = await operation();
            await audit.LogAsync(User.ActorName(), action, $"{target} → 成功", cancellationToken);
            return view;
        }
        catch (Exception ex) when (ex is DeploymentFailedException or CommandFailedException)
        {
            await audit.LogAsync(User.ActorName(), action, $"{target} → 失敗（元に戻しました）", CancellationToken.None);
            throw;
        }
    }
}
