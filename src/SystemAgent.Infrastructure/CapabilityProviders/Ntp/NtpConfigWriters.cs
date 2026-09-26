using System.Text;

namespace SystemAgent.Infrastructure.CapabilityProviders.Ntp;

/// <summary>
/// NTPサーバー設定の書き換え方式。テンプレートの settings.configStyle から名前で参照される。
/// いずれも入力テキスト→出力テキストの純粋関数（ファイル操作はプロバイダーが行う）。
/// </summary>
public interface INtpConfigWriter
{
    /// <summary>設定テキストから有効なサーバー指定を読む。</summary>
    IReadOnlyList<string> ReadServers(string config);

    /// <summary>既存の設定テキスト（無ければ空）に対し、サーバーを置き換えた新しいテキストを返す。</summary>
    string Apply(string config, IReadOnlyList<string> servers);
}

public static class NtpConfigWriters
{
    public static readonly IReadOnlyDictionary<string, INtpConfigWriter> ByStyle = new Dictionary<string, INtpConfigWriter>
    {
        ["chrony-managed-block"] = new ChronyManagedBlockWriter(),
        ["timesyncd-dropin"] = new TimesyncdDropInWriter(),
    };
}

/// <summary>
/// chrony.conf の既存 server/pool/peer 行を "#SystemAgent# " でコメントアウトし、末尾の管理ブロックに書く。
/// 元の行は消さないため、ブロックを削除してコメントを外せば手で戻せる。何度適用しても結果は同じ。
/// </summary>
public sealed class ChronyManagedBlockWriter : INtpConfigWriter
{
    public const string BeginMarker = "# BEGIN SystemAgent managed NTP servers (do not edit)";
    public const string EndMarker = "# END SystemAgent managed NTP servers";
    public const string DisabledPrefix = "#SystemAgent# ";
    private static readonly string[] SourceDirectives = ["server", "pool", "peer"];

    public IReadOnlyList<string> ReadServers(string config) =>
        config.Split('\n')
            .Select(l => l.Trim())
            .Select(l => l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Where(t => t.Length >= 2 && SourceDirectives.Contains(t[0]))
            .Select(t => t[1])
            .ToList();

    public string Apply(string config, IReadOnlyList<string> servers)
    {
        var output = new StringBuilder();
        var inBlock = false;
        foreach (var line in config.Replace("\r\n", "\n").Split('\n'))
        {
            if (line == BeginMarker) { inBlock = true; continue; }
            if (line == EndMarker) { inBlock = false; continue; }
            if (inBlock) continue;

            var first = line.TrimStart().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            output.Append(first is not null && SourceDirectives.Contains(first) ? DisabledPrefix + line : line).Append('\n');
        }

        var text = output.ToString().TrimEnd('\n') + "\n\n";
        text += BeginMarker + "\n";
        foreach (var server in servers) text += $"server {server} iburst\n";
        return text + EndMarker + "\n";
    }
}

/// <summary>systemd-timesyncd のドロップインファイル（/etc/systemd/timesyncd.conf.d/）を丸ごと生成する。</summary>
public sealed class TimesyncdDropInWriter : INtpConfigWriter
{
    public IReadOnlyList<string> ReadServers(string config) =>
        config.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("NTP=", StringComparison.Ordinal))
            .SelectMany(l => l[4..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToList();

    public string Apply(string config, IReadOnlyList<string> servers) =>
        "# Managed by SystemAgent (do not edit)\n[Time]\nNTP=" + string.Join(' ', servers) + "\n";
}
