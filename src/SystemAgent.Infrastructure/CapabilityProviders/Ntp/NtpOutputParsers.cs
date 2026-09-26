using System.Globalization;
using System.Text.RegularExpressions;
using SystemAgent.Core.CapabilityProviders;

namespace SystemAgent.Infrastructure.CapabilityProviders.Ntp;

/// <summary>status コマンドの解析結果。実装によって得られない項目はnull。</summary>
public sealed record NtpStatusPart(
    bool? Synchronized, string? CurrentSource, int? Stratum, double? OffsetSeconds, IReadOnlyList<string>? Servers);

/// <summary>NTP実装の出力パーサー。テンプレートの "parser" から名前で参照される。</summary>
public static partial class NtpOutputParsers
{
    public static readonly IReadOnlyDictionary<string, Func<string, NtpStatusPart>> Status =
        new Dictionary<string, Func<string, NtpStatusPart>>
        {
            ["chrony-tracking-csv"] = ParseChronyTracking,
            ["timesyncd-show"] = ParseTimesyncdShow,
        };

    public static readonly IReadOnlyDictionary<string, Func<string, IReadOnlyList<NtpSource>>> Sources =
        new Dictionary<string, Func<string, IReadOnlyList<NtpSource>>>
        {
            ["chrony-sources-csv"] = ParseChronySources,
        };

    /// <summary>
    /// `chronyc -c tracking`: RefID,名前,stratum,参照時刻,system time,last offset,...,leap status。
    /// system timeは「正ならシステム時刻が遅れている」ため符号を反転する。
    /// </summary>
    private static NtpStatusPart ParseChronyTracking(string output)
    {
        var f = output.Trim().Split(',');
        var leap = f.Length > 13 ? f[13] : "";
        var synchronized = leap.Length > 0 && !leap.Equals("Not synchronised", StringComparison.OrdinalIgnoreCase);
        return new NtpStatusPart(
            Synchronized: synchronized,
            CurrentSource: synchronized && f.Length > 1 && f[1].Length > 0 ? f[1] : null,
            Stratum: synchronized && int.TryParse(f.ElementAtOrDefault(2), out var stratum) ? stratum : null,
            OffsetSeconds: double.TryParse(f.ElementAtOrDefault(4), NumberStyles.Float, CultureInfo.InvariantCulture, out var slow) ? -slow : null,
            Servers: null);
    }

    /// <summary>`chronyc -c sources`: モード,状態,名前,stratum,poll,reach(8進),最終受信,調整後オフセット,...</summary>
    private static IReadOnlyList<NtpSource> ParseChronySources(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(','))
            .Where(f => f.Length >= 8)
            .Select(f => new NtpSource(
                Address: f[2],
                State: f[1] switch
                {
                    "*" => NtpSourceState.Selected,
                    "+" => NtpSourceState.Combined,
                    "-" => NtpSourceState.NotCombined,
                    "?" => NtpSourceState.Unreachable,
                    "x" => NtpSourceState.Falseticker,
                    "~" => NtpSourceState.TooVariable,
                    _ => NtpSourceState.Unknown,
                },
                Stratum: int.TryParse(f[3], out var stratum) ? stratum : null,
                OffsetSeconds: double.TryParse(f[7], NumberStyles.Float, CultureInfo.InvariantCulture, out var offset) ? offset : null,
                Reachable: f[5] != "0"))
            .ToList();

    /// <summary>`timedatectl show-timesync --all`（key=value）。同期状態は別コマンド(synchronized)で取る。</summary>
    private static NtpStatusPart ParseTimesyncdShow(string output)
    {
        var values = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Contains('='))
            .Select(l => (Key: l[..l.IndexOf('=')], Value: l[(l.IndexOf('=') + 1)..]))
            .GroupBy(x => x.Key)
            .ToDictionary(g => g.Key, g => g.Last().Value);

        string[] Split(string key) => values.GetValueOrDefault(key, "").Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var configured = Split("SystemNTPServers").Concat(Split("RuntimeNTPServers")).Concat(Split("LinkNTPServers")).Distinct().ToList();
        var serverName = values.GetValueOrDefault("ServerName", "");
        var serverAddress = values.GetValueOrDefault("ServerAddress", "");
        var stratum = StratumInMessage().Match(values.GetValueOrDefault("NTPMessage", ""));

        return new NtpStatusPart(
            Synchronized: null,
            CurrentSource: serverName.Length == 0 ? null : serverAddress.Length > 0 ? $"{serverName} ({serverAddress})" : serverName,
            Stratum: stratum.Success ? int.Parse(stratum.Groups[1].Value) : null,
            OffsetSeconds: null,
            // 明示設定が無い場合はフォールバックサーバーが使われる
            Servers: configured.Count > 0 ? configured : Split("FallbackNTPServers"));
    }

    [GeneratedRegex(@"Stratum=(\d+)")]
    private static partial Regex StratumInMessage();
}
