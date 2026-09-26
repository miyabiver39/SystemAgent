using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using SystemAgent.Core.CapabilityProviders;
using SystemAgent.Infrastructure.CapabilityProviders.Templates;
using SystemAgent.Infrastructure.Commands;

namespace SystemAgent.Infrastructure.CapabilityProviders;

/// <summary>
/// OS情報と管理対象ツールの有無・バージョンを検出する（基本設計書 7章 EnvironmentDetector）。
/// 結果はプロセス内でキャッシュし、refresh指定時のみ再検出する。
/// </summary>
public sealed class EnvironmentDetector(ICommandRunner runner, ILogger<EnvironmentDetector> logger) : IEnvironmentDetector
{
    /// <summary>検出対象ツールとバージョン取得用の引数。新しい管理対象を増やすときはここに追加する。</summary>
    public static readonly IReadOnlyDictionary<string, string[]> KnownTools = new Dictionary<string, string[]>
    {
        ["podman"] = ["--version"],
        ["docker"] = ["--version"],
        ["systemctl"] = ["--version"],
        ["chronyc"] = ["--version"],
        ["nmcli"] = ["--version"],
        ["keepalived"] = ["--version"],
    };

    private readonly SemaphoreSlim _lock = new(1, 1);
    private HostEnvironment? _cached;

    public async Task<HostEnvironment> DetectAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        if (_cached is not null && !refresh) return _cached;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_cached is not null && !refresh) return _cached;
            _cached = await DetectCoreAsync(cancellationToken);
            logger.LogInformation("環境を検出しました: {Os} {Version} / ツール: {Tools}",
                _cached.OsId, _cached.OsVersion, string.Join(", ", _cached.Tools.Select(t => $"{t.Name} {t.Version}")));
            return _cached;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<HostEnvironment> DetectCoreAsync(CancellationToken cancellationToken)
    {
        var osRelease = File.Exists("/etc/os-release")
            ? ParseOsRelease(await File.ReadAllLinesAsync("/etc/os-release", cancellationToken))
            : new Dictionary<string, string>();

        var tools = new List<ToolInfo>();
        foreach (var (name, args) in KnownTools)
        {
            var result = await runner.RunAsync(name, args, TimeSpan.FromSeconds(10), cancellationToken);
            if (result.ExitCode != 0) continue;
            tools.Add(new ToolInfo(name, VersionText.Parse(result.StandardOutput + result.StandardError).ToString()));
        }

        return new HostEnvironment(
            HostName: Environment.MachineName,
            OsId: osRelease.GetValueOrDefault("ID", RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows" : "unknown"),
            OsIdLike: osRelease.GetValueOrDefault("ID_LIKE", "").Split(' ', StringSplitOptions.RemoveEmptyEntries),
            OsVersion: osRelease.GetValueOrDefault("VERSION_ID", Environment.OSVersion.Version.ToString()),
            OsPrettyName: osRelease.GetValueOrDefault("PRETTY_NAME", RuntimeInformation.OSDescription),
            Architecture: RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => "x86_64",
                Architecture.Arm64 => "aarch64",
                var other => other.ToString().ToLowerInvariant(),
            },
            Tools: tools);
    }

    public static Dictionary<string, string> ParseOsRelease(IEnumerable<string> lines) =>
        lines.Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#') && l.Contains('='))
            .Select(l => (Key: l[..l.IndexOf('=')], Value: l[(l.IndexOf('=') + 1)..].Trim('"', '\'')))
            .GroupBy(x => x.Key)
            .ToDictionary(g => g.Key, g => g.Last().Value);
}
