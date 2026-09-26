using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
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
    /// <param name="VersionFrom">自身でバージョンを出さないツールのバージョンを、別ツールの検出結果から取る。</param>
    /// <param name="VersionPattern">出力からバージョンを取り出す正規表現（グループ1）。省略時は最初の数字列。</param>
    public sealed record ToolProbe(string Executable, string[] Args, string? VersionFrom = null, string? VersionPattern = null);

    /// <summary>
    /// 検出対象ツール。コマンドが終了コード0で終われば「あり」とし、出力からバージョンを取る。
    /// 新しい管理対象を増やすときはここに追加する（順序はVersionFromの参照先が先になるようにする）。
    /// </summary>
    public static readonly IReadOnlyList<(string Name, ToolProbe Probe)> KnownTools =
    [
        ("podman", new("podman", ["--version"])),
        ("docker", new("docker", ["--version"])),
        ("systemctl", new("systemctl", ["--version"])),
        ("chronyc", new("chronyc", ["--version"])),
        // systemd-timesyncdは稼働中の場合のみ「あり」（chrony導入時は停止・削除されている）
        ("timesyncd", new("systemctl", ["is-active", "--quiet", "systemd-timesyncd"], VersionFrom: "systemctl")),
        ("nmcli", new("nmcli", ["--version"])),
        // 出力例: "ip utility, iproute2-6.2.0" / 古い版は "iproute2-ss200127"
        ("ip", new("ip", ["-V"], VersionPattern: @"iproute2-(?:ss)?([\d.]+)")),
        ("keepalived", new("keepalived", ["--version"])),
    ];

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
        foreach (var (name, probe) in KnownTools)
        {
            var result = await runner.RunAsync(probe.Executable, probe.Args, TimeSpan.FromSeconds(10), cancellationToken);
            if (result.ExitCode != 0) continue;
            var output = result.StandardOutput + result.StandardError;
            var version = probe switch
            {
                { VersionFrom: { } source } => tools.FirstOrDefault(t => t.Name == source)?.Version ?? "0.0",
                { VersionPattern: { } pattern } => VersionText.Parse(Regex.Match(output, pattern).Groups[1].Value).ToString(),
                _ => VersionText.Parse(output).ToString(),
            };
            tools.Add(new ToolInfo(name, version));
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
