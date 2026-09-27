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
    ContainerRuntimeResolver resolver, RegistryService registries, ImageImporter importer, IAuditLogger audit) : ControllerBase
{
    [HttpGet]
    public async Task<IReadOnlyList<ImageInfo>> List(CancellationToken cancellationToken) =>
        await (await resolver.ResolveAsync(cancellationToken)).ListImagesAsync(cancellationToken);

    [HttpPost("pull")]
    public async Task<IActionResult> Pull(PullImageRequest request, CancellationToken cancellationToken)
    {
        var runtime = await resolver.ResolveAsync(cancellationToken);
        var tlsVerify = await registries.EnsureLoginAsync(runtime, request.Image, cancellationToken);
        await runtime.PullImageAsync(request.Image, tlsVerify, cancellationToken);
        await audit.LogAsync(User.Identity!.Name!, "image.pull", $"{runtime.Runtime.Name}: {request.Image}", cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// ローカルのイメージをレジストリへ送る（ADR-026）。送り先の名前を一時的に付けて push し、元から無かった名前なら外す。
    /// 送り先のレジストリは登録済みであること（登録済みの認証情報でログインしてから送る）。
    /// </summary>
    [HttpPost("push")]
    public async Task<IActionResult> Push(PushImageRequest request, CancellationToken cancellationToken)
    {
        var image = request.Image.Trim();
        var target = request.Target.Trim();
        var registry = RegistryName.FromImage(target);
        if (registry == RegistryName.DockerHub && !target.StartsWith("docker.io/", StringComparison.Ordinal))
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                detail: "送り先はレジストリを含めて指定してください（例: zot.example.com:5000/app/web:1.0）。");
        if (registries.Find(registry) is null)
            return Problem(statusCode: StatusCodes.Status400BadRequest, detail: $"レジストリ {registry} は登録されていません。先に「レジストリ」で登録してください。");

        var runtime = await resolver.ResolveAsync(cancellationToken);
        var tlsVerify = await registries.EnsureLoginAsync(runtime, target, cancellationToken);
        // 名前の無いイメージ（IDで指定）に付けた名前を外すとイメージ自体が消えるため、その場合は付けた名前を残す
        var source = (await runtime.ListImagesAsync(cancellationToken)).FirstOrDefault(i =>
            i.Tags.Contains(image) || i.Id.StartsWith(image.Replace("sha256:", ""), StringComparison.Ordinal)
            || i.Id.Replace("sha256:", "").StartsWith(image.Replace("sha256:", ""), StringComparison.Ordinal));
        var tagged = image != target && source is not { Tags.Count: 0 } && !await runtime.ImageExistsAsync(target, cancellationToken);
        if (image != target) await runtime.TagImageAsync(image, target, cancellationToken);
        try
        {
            await runtime.PushImageAsync(target, tlsVerify, cancellationToken);
        }
        finally
        {
            // 送るためだけに付けた名前は外す（イメージ本体は元の名前で残る）
            if (tagged) await runtime.RemoveImageAsync(target, CancellationToken.None);
        }
        await audit.LogAsync(User.Identity!.Name!, "image.push", $"{runtime.Runtime.Name}: {image} → {target}", cancellationToken);
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
            return new ImportImageResponse(await importer.ImportAsync(Request.Body, name, User.Identity!.Name!, cancellationToken));
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
            return new ImportImageResponse(await importer.ImportAsync(section.Body, fileName, User.Identity!.Name!, cancellationToken));
        }
        return Problem(statusCode: StatusCodes.Status400BadRequest, detail: "ファイルが含まれていません。");
    }

    /// <summary>octet-stream で取り込む場合のファイル名（URLエンコード）。</summary>
    public const string FileNameHeader = "X-File-Name";
}
