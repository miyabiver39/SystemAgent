using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using SystemAgent.Core.Auditing;
using SystemAgent.Core.CapabilityProviders;
using SystemAgent.Core.Contracts;
using SystemAgent.Infrastructure.CapabilityProviders.Containers;
using SystemAgent.Web.Api;
using SystemAgent.Web.Auth;

namespace SystemAgent.Web.Controllers;

/// <summary>このノードのコンテナイメージ管理。エアギャップ環境向けにイメージアーカイブ(tar)の取り込みに対応する。</summary>
[ApiController]
[Authorize]
[Route("api/images")]
public class ImagesController(
    ContainerRuntimeResolver resolver, ImageTransferService transfer, ImageImporter importer, IAuditLogger audit) : ControllerBase
{
    [HttpGet]
    public async Task<IReadOnlyList<ImageInfo>> List(CancellationToken cancellationToken) =>
        await (await resolver.ResolveAsync(cancellationToken)).ListImagesAsync(cancellationToken);

    [HttpPost("pull")]
    public async Task<IActionResult> Pull(PullImageRequest request, CancellationToken cancellationToken)
    {
        var runtime = await resolver.ResolveAsync(cancellationToken);
        await transfer.PullAsync(runtime, request.Image, cancellationToken);
        await audit.LogAsync(User.ActorName(), "image.pull", $"{runtime.Runtime.Name}: {request.Image}", cancellationToken);
        return NoContent();
    }

    /// <summary>ローカルのイメージを登録済みのレジストリへ送る（ADR-026）。</summary>
    [HttpPost("push")]
    public async Task<IActionResult> Push(PushImageRequest request, CancellationToken cancellationToken)
    {
        var runtime = await resolver.ResolveAsync(cancellationToken);
        await transfer.PushAsync(runtime, request.Image, request.Target, cancellationToken);
        await audit.LogAsync(User.ActorName(), "image.push", $"{runtime.Runtime.Name}: {request.Image.Trim()} → {request.Target.Trim()}", cancellationToken);
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
        await audit.LogAsync(User.ActorName(), "image.remove", $"{runtime.Runtime.Name}: {id}", cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// イメージアーカイブ(tar)を取り込む（podman/docker load）。次のどちらかで送る。
    /// <list type="bullet">
    /// <item>multipart/form-data の最初のファイル（CLI）</item>
    /// <item>application/octet-stream の本文そのもの。ファイル名は X-File-Name ヘッダ（URLエンコード）。WebUIからの転送用</item>
    /// </list>
    /// </summary>
    [HttpPost("import")]
    [DisableRequestSizeLimit]
    [DisableFormValueModelBinding]
    public async Task<ActionResult<ImportImageResponse>> Import(CancellationToken cancellationToken)
    {
        if (!MediaTypeHeaderValue.TryParse(Request.ContentType, out var contentType))
            return Problem(statusCode: StatusCodes.Status400BadRequest, detail: "multipart/form-data または application/octet-stream でファイルを送信してください。");

        if (contentType.MediaType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            var name = Uri.UnescapeDataString(Request.Headers[FileNameHeader].FirstOrDefault() ?? "image.tar");
            return new ImportImageResponse(await importer.ImportAsync(Request.Body, name, User.ActorName(), cancellationToken));
        }

        if (!contentType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
            || HeaderUtilities.RemoveQuotes(contentType.Boundary).Value is not { Length: > 0 } boundary)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, detail: "multipart/form-data または application/octet-stream でファイルを送信してください。");
        }

        var reader = new MultipartReader(boundary, Request.Body);
        while (await reader.ReadNextSectionAsync(cancellationToken) is { } section)
        {
            if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
                || !disposition.IsFileDisposition()) continue;

            var fileName = disposition.FileName.Value ?? disposition.FileNameStar.Value ?? "image.tar";
            return new ImportImageResponse(await importer.ImportAsync(section.Body, fileName, User.ActorName(), cancellationToken));
        }
        return Problem(statusCode: StatusCodes.Status400BadRequest, detail: "ファイルが含まれていません。");
    }

    /// <summary>octet-stream で取り込む場合のファイル名（URLエンコード）。</summary>
    public const string FileNameHeader = "X-File-Name";
}
