using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SystemAgent.Core.CapabilityProviders;
using SystemAgent.Infrastructure.CapabilityProviders.Templates;

namespace SystemAgent.Infrastructure.CapabilityProviders.Ntp;

/// <summary>
/// コマンドテンプレートに従ってNTP実装（chrony / systemd-timesyncd）を操作する。
/// 設定変更は「初回のみ元ファイルを退避 → 原子的に書き換え → サービス再起動」で行い、再起動に失敗したら元に戻す。
/// </summary>
public sealed partial class TemplateNtpProvider(TemplateCommandExecutor executor, ILogger logger) : INtpProvider
{
    public const string BackupSuffix = ".systemagent.orig";
    public const int MaxServers = 10;

    private INtpConfigWriter Writer => NtpConfigWriters.ByStyle[executor.Setting("configStyle")];
    private string ConfigFile => executor.Setting("configFile");

    public NtpImplementationInfo Implementation => new(
        executor.Setting("implementation"), executor.Template.Id, ConfigFile, executor.Setting("service"));

    public async Task<NtpStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var part = NtpOutputParsers.Status[executor.Parser("status")!](
            (await executor.RunAsync("status", null, cancellationToken)).StandardOutput);

        var synchronized = part.Synchronized
            ?? (executor.Has("synchronized")
                && (await executor.RunAsync("synchronized", null, cancellationToken)).StandardOutput.Trim() == "yes");

        IReadOnlyList<NtpSource> sources = executor.Has("sources")
            ? NtpOutputParsers.Sources[executor.Parser("sources")!]((await executor.RunAsync("sources", null, cancellationToken)).StandardOutput)
            : part.CurrentSource is { } current ? [new NtpSource(current, NtpSourceState.Selected, part.Stratum, null, true)] : [];

        var configured = File.Exists(ConfigFile) ? Writer.ReadServers(await File.ReadAllTextAsync(ConfigFile, cancellationToken)) : [];
        if (configured.Count == 0) configured = part.Servers ?? [];

        return new NtpStatus(Implementation.Name, synchronized, configured, part.CurrentSource, part.Stratum, part.OffsetSeconds, sources);
    }

    public async Task SetServersAsync(IReadOnlyList<string> servers, CancellationToken cancellationToken = default)
    {
        var validated = Validate(servers);
        var original = File.Exists(ConfigFile) ? await File.ReadAllTextAsync(ConfigFile, cancellationToken) : null;

        if (original is not null && !File.Exists(ConfigFile + BackupSuffix))
        {
            await WriteAtomicAsync(ConfigFile + BackupSuffix, original, cancellationToken);
        }
        await WriteAtomicAsync(ConfigFile, Writer.Apply(original ?? "", validated), cancellationToken);

        try
        {
            await executor.RunAsync("restartService", null, cancellationToken);
        }
        catch (CommandFailedException)
        {
            logger.LogWarning("NTPサービスの再起動に失敗したため、{File} を元に戻します。", ConfigFile);
            if (original is null) File.Delete(ConfigFile);
            else await WriteAtomicAsync(ConfigFile, original, CancellationToken.None);
            try
            {
                await executor.RunAsync("restartService", null, CancellationToken.None);
            }
            catch (CommandFailedException ex)
            {
                logger.LogError(ex, "設定を戻した後のNTPサービス再起動にも失敗しました。");
            }
            throw;
        }
    }

    public Task SyncNowAsync(CancellationToken cancellationToken = default) =>
        executor.RunAsync("syncNow", null, cancellationToken);

    public static IReadOnlyList<string> Validate(IReadOnlyList<string> servers)
    {
        var trimmed = servers.Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (trimmed.Count is 0 or > MaxServers)
            throw new ArgumentException($"NTPサーバーは1〜{MaxServers}件で指定してください。");
        foreach (var server in trimmed)
        {
            if (server.Length > 253 || !(IPAddress.TryParse(server, out _) || HostName().IsMatch(server)))
                throw new ArgumentException($"NTPサーバーの指定が不正です: {server}");
        }
        return trimmed;
    }

    private static async Task WriteAtomicAsync(string path, string content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        }
        await using (var stream = new FileStream(tmp, options))
        await using (var writer = new StreamWriter(stream))
        {
            await writer.WriteAsync(content.AsMemory(), cancellationToken);
        }
        File.Move(tmp, path, overwrite: true);
    }

    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9.-]*[A-Za-z0-9])?$")]
    private static partial Regex HostName();
}
