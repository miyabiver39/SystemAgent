using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using SystemAgent.Core.Auditing;
using SystemAgent.Core.CapabilityProviders;
using SystemAgent.Core.Contracts;
using SystemAgent.Infrastructure.CapabilityProviders.Containers;
using SystemAgent.Web.Api;

namespace SystemAgent.Web.Controllers;

/// <summary>このノードのコンテナイメージ管理。エアギャップ環境向けにイメージアーカイブ(tar)の取り込みに対応する。</summary>
[ApiController]
[Authorize]
[Route("api/images")]
public class ImagesController(
    ContainerRuntimeResolver resolver, RegistryService registries, IAuditLogger audit, IConfiguration configuration, ILogger<ImagesController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<IReadOnlyList<ImageInfo>> List(CancellationToken cancellationToken) =>
        await (await resolver.ResolveAsync(cancellationToken)).ListImagesAsync(cancellationToken);

    [HttpPost("pull")]
    public async Task<IActionResult> Pull(PullImageRequest request, CancellationToken cancellationToken)
    {
        var runtime = await resolver.ResolveAsync(cancellationToken);
        await registries.EnsureLoginAsync(runtime, request.Image, cancellationToken);
        await runtime.PullImageAsync(request.Image, cancellationToken);
        await audit.LogAsync(User.Identity!.Name!, "image.pull", $"{runtime.Runtime.Name}: {request.Image}", cancellationToken);
        return NoContent();
    }

    /// <param name="id">イメージIDまたはタグ（"/" を含むためURLエンコードして渡す）。</param>
    [HttpDelete("{**id}")]
    public async Task<IActionResult> Remove(string id, CancellationToken cancellationToken)
    {
        // catch-allルートは %2F をデコードしないため明示的に戻す
        id = Uri.UnescapeDataString(id);
        var runtime = await resolver.ResolveAsync(cancellationToken);
        await runtime.RemoveImageAsync(id, cancellationToken);
        await audit.LogAsync(User.Identity!.Name!, "image.remove", $"{runtime.Runtime.Name}: {id}", cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// multipart/form-data の最初のファイルをイメージアーカイブとして取り込む（podman/docker load）。
    /// 数GBのファイルを想定し、メモリに載せず一時ファイルへストリーム書き込みする。
    /// </summary>
    [HttpPost("import")]
    [DisableRequestSizeLimit]
    [DisableFormValueModelBinding]
    public async Task<ActionResult<ImportImageResponse>> Import(CancellationToken cancellationToken)
    {
        if (!MediaTypeHeaderValue.TryParse(Request.ContentType, out var contentType)
            || !contentType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
            || HeaderUtilities.RemoveQuotes(contentType.Boundary).Value is not { Length: > 0 } boundary)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, detail: "multipart/form-data でファイルを送信してください。");
        }

        var runtime = await resolver.ResolveAsync(cancellationToken);
        var tempPath = Path.Combine(ImportDirectory(), $"systemagent-import-{Guid.NewGuid():N}.tar");
        try
        {
            string? fileName = null;
            var reader = new MultipartReader(boundary, Request.Body);
            while (await reader.ReadNextSectionAsync(cancellationToken) is { } section)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
                    || !disposition.IsFileDisposition()) continue;

                fileName = disposition.FileName.Value ?? disposition.FileNameStar.Value;
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                await using (var file = new FileStream(tempPath, options))
                {
                    await section.Body.CopyToAsync(file, cancellationToken);
                }
                break;
            }
            if (fileName is null)
            {
                return Problem(statusCode: StatusCodes.Status400BadRequest, detail: "ファイルが含まれていません。");
            }

            logger.LogInformation("イメージアーカイブを取り込みます: {File} ({Bytes} bytes)", fileName, new FileInfo(tempPath).Length);
            var output = await runtime.LoadImageAsync(tempPath, cancellationToken);
            await audit.LogAsync(User.Identity!.Name!, "image.import", $"{runtime.Runtime.Name}: {fileName} → {output}", cancellationToken);
            return new ImportImageResponse(output);
        }
        finally
        {
            System.IO.File.Delete(tempPath);
        }
    }

    // /tmp はtmpfsで容量が小さいことがあるため、Linuxの既定は /var/tmp
    private string ImportDirectory() =>
        configuration["Container:ImportTempPath"] ?? (OperatingSystem.IsLinux() ? "/var/tmp" : Path.GetTempPath());
}
