using SystemAgent.Core.Auditing;
using SystemAgent.Infrastructure.CapabilityProviders.Containers;

namespace SystemAgent.Web.Api;

/// <summary>
/// イメージアーカイブ(tar)をこのノードのランタイムに取り込む（podman/docker load）。
/// 数GBのファイルを想定し、メモリに載せず一時ファイルへストリーム書き込みしてから読み込む。
/// </summary>
public sealed class ImageImporter(
    ContainerRuntimeResolver resolver, IAuditLogger audit, IConfiguration configuration, ILogger<ImageImporter> logger)
{
    /// <returns>ランタイムの出力（読み込んだイメージ名等）。</returns>
    public async Task<string> ImportAsync(Stream archive, string fileName, string actor, CancellationToken cancellationToken)
    {
        var runtime = await resolver.ResolveAsync(cancellationToken);
        var tempPath = Path.Combine(ImportDirectory(), $"systemagent-import-{Guid.NewGuid():N}.tar");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var file = new FileStream(tempPath, options))
            {
                await archive.CopyToAsync(file, cancellationToken);
            }

            logger.LogInformation("イメージアーカイブを取り込みます: {File} ({Bytes} bytes)", fileName, new FileInfo(tempPath).Length);
            var output = await runtime.LoadImageAsync(tempPath, cancellationToken);
            await audit.LogAsync(actor, "image.import", $"{runtime.Runtime.Name}: {fileName} → {output}", cancellationToken);
            return output;
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    // /tmp はtmpfsで容量が小さいことがあるため、Linuxの既定は /var/tmp
    private string ImportDirectory() =>
        configuration["Container:ImportTempPath"] ?? (OperatingSystem.IsLinux() ? "/var/tmp" : Path.GetTempPath());
}
