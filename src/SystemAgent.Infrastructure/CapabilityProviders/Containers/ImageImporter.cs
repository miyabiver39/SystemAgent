using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SystemAgent.Core.Auditing;
using SystemAgent.Core.Errors;

namespace SystemAgent.Infrastructure.CapabilityProviders.Containers;

/// <summary>
/// イメージアーカイブ(tar)をこのノードのランタイムに取り込む（podman/docker load）。
/// 数GBのファイルを想定し、メモリに載せず一時ファイルへストリーム書き込みしてから読み込む。
/// ホストのディスクを使い切らないよう、大きさの上限（Container:MaxImportSizeBytes、既定20GB）と、書き込み前の空き容量
/// （受け取る大きさ + Container:ImportMinFreeBytes、既定1GB）を確認する。上限を超えたら書き込みを中断して一時ファイルを消す。
/// </summary>
public sealed class ImageImporter(
    IContainerRuntimeResolver resolver, IAuditLogger audit, IConfiguration configuration, ILogger<ImageImporter> logger)
{
    public const long DefaultMaxImportSizeBytes = 20L * 1024 * 1024 * 1024;
    public const long DefaultMinFreeBytes = 1L * 1024 * 1024 * 1024;

    public long MaxImportSizeBytes => configuration.GetValue("Container:MaxImportSizeBytes", DefaultMaxImportSizeBytes);

    public long MinFreeBytes => Math.Max(0, configuration.GetValue("Container:ImportMinFreeBytes", DefaultMinFreeBytes));

    /// <param name="declaredLength">送信側が申告した大きさ（Content-Length）。分かれば書き込む前に上限・空き容量を確認する。</param>
    /// <returns>ランタイムの出力（読み込んだイメージ名等）。</returns>
    public async Task<string> ImportAsync(Stream archive, string fileName, string actor, CancellationToken cancellationToken,
        long? declaredLength = null)
    {
        if (declaredLength > MaxImportSizeBytes) throw TooLarge();
        var directory = ImportDirectory();
        EnsureFreeSpace(directory, declaredLength ?? 0);

        var runtime = await resolver.ResolveAsync(cancellationToken);
        var tempPath = Path.Combine(directory, $"systemagent-import-{Guid.NewGuid():N}.tar");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var file = new FileStream(tempPath, options))
            {
                await CopyWithLimitAsync(archive, file, cancellationToken);
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

    /// <summary>上限を超えた時点で中断する（申告のない chunked 転送や、申告より大きい本文に備える）。</summary>
    private async Task CopyWithLimitAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > MaxImportSizeBytes) throw TooLarge();
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private PayloadTooLargeException TooLarge() =>
        new($"イメージアーカイブが上限（{MaxImportSizeBytes / (1024 * 1024)} MB）を超えています。上限は Container:MaxImportSizeBytes で変更できます。");

    private void EnsureFreeSpace(string directory, long incoming)
    {
        long available;
        try
        {
            // Windows はドライブ単位、Linux はディレクトリが載っているファイルシステムの空き容量
            var root = OperatingSystem.IsWindows() ? Path.GetPathRoot(Path.GetFullPath(directory))! : directory;
            available = new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            logger.LogWarning("取り込み用の一時ディレクトリ {Directory} の空き容量を確認できません: {Message}", directory, ex.Message);
            return;
        }
        if (available - incoming < MinFreeBytes)
            throw new InsufficientStorageException(
                $"取り込み用の一時ディレクトリ {directory} の空き容量が足りません（空き {available / (1024 * 1024)} MB、" +
                $"必要 {(incoming + MinFreeBytes) / (1024 * 1024)} MB）。Container:ImportTempPath で別の場所を指定できます。");
    }

    // /tmp はtmpfsで容量が小さいことがあるため、Linuxの既定は /var/tmp
    private string ImportDirectory() =>
        configuration["Container:ImportTempPath"] ?? (OperatingSystem.IsLinux() ? "/var/tmp" : Path.GetTempPath());
}
