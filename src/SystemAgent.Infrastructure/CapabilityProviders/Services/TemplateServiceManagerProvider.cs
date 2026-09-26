using System.Globalization;
using System.Text.RegularExpressions;
using SystemAgent.Core.CapabilityProviders;
using SystemAgent.Infrastructure.CapabilityProviders.Templates;

namespace SystemAgent.Infrastructure.CapabilityProviders.Services;

public static partial class ServiceOutputParsers
{
    public static readonly IReadOnlyDictionary<string, Func<string, ServiceStatus>> Status =
        new Dictionary<string, Func<string, ServiceStatus>>
        {
            ["systemctl-show"] = ParseSystemctlShow,
        };

    /// <summary>`systemctl show <unit> --property=...`（key=value）。</summary>
    private static ServiceStatus ParseSystemctlShow(string output)
    {
        var values = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Contains('='))
            .Select(l => (Key: l[..l.IndexOf('=')], Value: l[(l.IndexOf('=') + 1)..]))
            .GroupBy(x => x.Key)
            .ToDictionary(g => g.Key, g => g.Last().Value);
        string Get(string key) => values.GetValueOrDefault(key, "");

        var loadState = Get("LoadState");
        var id = Get("Id");
        return new ServiceStatus(
            Name: id.EndsWith(".service", StringComparison.Ordinal) ? id[..^".service".Length] : id,
            Description: Get("Description"),
            Exists: loadState is not ("not-found" or ""),
            LoadState: loadState,
            ActiveState: Get("ActiveState"),
            SubState: Get("SubState"),
            UnitFileState: Get("UnitFileState"),
            MainPid: int.TryParse(Get("MainPID"), out var pid) && pid > 0 ? pid : null,
            ActiveSince: ParseTimestamp(Get("ActiveEnterTimestamp")));
    }

    /// <summary>"Sat 2026-09-26 14:35:51 JST"。タイムゾーン名は曖昧なため、このノードのローカル時刻として扱う。</summary>
    public static DateTimeOffset? ParseTimestamp(string text)
    {
        var match = Timestamp().Match(text);
        if (!match.Success) return null;
        var local = DateTime.ParseExact(match.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal);
        return new DateTimeOffset(local);
    }

    [GeneratedRegex(@"(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})")]
    private static partial Regex Timestamp();
}

/// <summary>コマンドテンプレートに従ってサービスを操作する。</summary>
public sealed partial class TemplateServiceManagerProvider(TemplateCommandExecutor executor) : IServiceManagerProvider
{
    public async Task<ServiceStatus> GetStatusAsync(string unit, CancellationToken cancellationToken = default) =>
        ServiceOutputParsers.Status[executor.Parser("status")!](
            (await executor.RunAsync("status", Values(unit), cancellationToken)).StandardOutput);

    public Task ExecuteAsync(string unit, ServiceAction action, CancellationToken cancellationToken = default) =>
        executor.RunAsync(action.ToString().ToLowerInvariant(), Values(unit), cancellationToken);

    public async Task<string> GetLogsAsync(string unit, int lines, CancellationToken cancellationToken = default)
    {
        if (lines is < 1 or > 5000) throw new ArgumentOutOfRangeException(nameof(lines), "行数は1〜5000で指定してください。");
        var values = Values(unit);
        values["lines"] = lines.ToString(CultureInfo.InvariantCulture);
        return (await executor.RunAsync("logs", values, cancellationToken)).StandardOutput;
    }

    public static string ValidateUnit(string unit) =>
        unit.Length <= 200 && UnitName().IsMatch(unit) ? unit : throw new ArgumentException($"サービス名が不正です: {unit}");

    private static Dictionary<string, string> Values(string unit) => new() { ["unit"] = ValidateUnit(unit) };

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9@._:-]*$")]
    private static partial Regex UnitName();
}
