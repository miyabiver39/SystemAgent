using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SystemAgent.Core.Auditing;
using SystemAgent.Core.Contracts;
using SystemAgent.Infrastructure.Backup;
using SystemAgent.Web.Auth;

namespace SystemAgent.Web.Controllers;

/// <summary>中央DBのバックアップ（このノードの保存先）。ADR-010 / ADR-020。</summary>
[ApiController]
[Authorize]
[Route("api/backups")]
public class BackupsController(BackupService backups, IAuditLogger audit) : ControllerBase
{
    [HttpGet]
    public async Task<BackupListResponse> List(CancellationToken cancellationToken) =>
        new(await backups.GetSettingsAsync(cancellationToken), backups.List());

    [HttpPost]
    public async Task<BackupFileInfo> Create(CancellationToken cancellationToken)
    {
        var created = await backups.CreateAsync(cancellationToken);
        await audit.LogAsync(User.ActorName(), "backup.create", created.Name, cancellationToken);
        return created;
    }

    /// <summary>ダウンロード。中身はDBの全データのため監査ログに残す。</summary>
    [HttpGet("{name}")]
    public async Task<IActionResult> Download(string name, CancellationToken cancellationToken)
    {
        if (!BackupService.IsValidName(name) || !backups.List().Any(b => b.Name == name)) return NotFound();
        await audit.LogAsync(User.ActorName(), "backup.download", name, cancellationToken);
        return File(backups.OpenRead(name), "application/gzip", name);
    }

    [HttpDelete("{name}")]
    public async Task<IActionResult> Delete(string name, CancellationToken cancellationToken)
    {
        if (!backups.Delete(name)) return NotFound();
        await audit.LogAsync(User.ActorName(), "backup.delete", name, cancellationToken);
        return NoContent();
    }

    /// <summary>バックアップでDBを置き換える。誤操作防止のため、ファイル名をもう一度指定させる。</summary>
    [HttpPost("{name}/restore")]
    public async Task<IActionResult> Restore(string name, RestoreBackupRequest request, CancellationToken cancellationToken)
    {
        if (request.Confirm != name)
            return Problem(statusCode: StatusCodes.Status400BadRequest, detail: "確認のため、復元するファイル名を正確に指定してください。");
        if (!backups.List().Any(b => b.Name == name)) return NotFound();

        await backups.RestoreAsync(name, cancellationToken);
        await audit.LogAsync(User.ActorName(), "backup.restore", name, cancellationToken);
        return NoContent();
    }
}
