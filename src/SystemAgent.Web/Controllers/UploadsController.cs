using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SystemAgent.Core.Contracts;
using SystemAgent.Web.Api;

namespace SystemAgent.Web.Controllers;

/// <summary>
/// WebUIのドラッグ＆ドロップによるイメージ取り込み。ブラウザから本文をそのまま受け取る（SignalRを通さないため数GBでも速く、進捗も出せる）。
/// 認証はWebUIがサーバー側で発行した使い捨てチケット（UploadTickets）で行う。取り込み先が他ノードならmTLSで転送する。
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/uploads")]
public class UploadsController(UploadTickets tickets, ImageImporter importer, NodeForwarder forwarder) : ControllerBase
{
    [HttpPost("images/{ticket}")]
    [DisableRequestSizeLimit]
    [DisableFormValueModelBinding]
    public async Task<IActionResult> Image(string ticket, CancellationToken cancellationToken)
    {
        if (tickets.Redeem(ticket) is not { } entry)
            return Problem(statusCode: StatusCodes.Status403Forbidden, detail: "アップロードの受付期限が切れました。画面を再読み込みしてやり直してください。");
        var fileName = Request.Headers[ImagesController.FileNameHeader].FirstOrDefault() ?? "image.tar";

        if (entry.NodeId is not { } nodeId)
            return Ok(new ImportImageResponse(await importer.ImportAsync(Request.Body, Uri.UnescapeDataString(fileName), entry.Actor, cancellationToken)));

        var content = new StreamContent(Request.Body);
        content.Headers.TryAddWithoutValidation("Content-Type", "application/octet-stream");
        if (Request.ContentLength is { } length) content.Headers.ContentLength = length;
        content.Headers.TryAddWithoutValidation(ImagesController.FileNameHeader, fileName);

        HttpResponseMessage response;
        try
        {
            response = await forwarder.SendAsync(nodeId, HttpMethod.Post, "api/images/import", content, "application/json", entry.Actor, cancellationToken);
        }
        catch (NodeForwarder.ForwardException ex)
        {
            return Problem(statusCode: ex.StatusCode, detail: ex.Message);
        }
        using (response)
        {
            Response.StatusCode = (int)response.StatusCode;
            Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
            await response.Content.CopyToAsync(Response.Body, cancellationToken);
        }
        return new EmptyResult();
    }
}
