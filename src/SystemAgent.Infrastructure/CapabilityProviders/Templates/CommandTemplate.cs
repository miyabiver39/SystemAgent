using System.Text.RegularExpressions;

namespace SystemAgent.Infrastructure.CapabilityProviders.Templates;

/// <summary>
/// OS/ツール/バージョンごとのコマンドテンプレート（基本設計書 7章、ADR-005）。
/// スキーマは CommandTemplates/command-template.schema.json。新しいOS/バージョン対応はテンプレート追加で行う。
/// </summary>
public sealed record CommandTemplate
{
    public required string Id { get; init; }

    /// <summary>対応するCapability（例: container-runtime）。TemplateRequirementsに定義されたもの。</summary>
    public required string Capability { get; init; }

    public string? Description { get; init; }

    public required string Executable { get; init; }

    public required TemplateMatch Match { get; init; }

    public required Dictionary<string, CommandDefinition> Commands { get; init; }

    /// <summary>読み込み元ファイル（エラー表示用）。</summary>
    public string SourcePath { get; init; } = "";
}

public sealed record TemplateMatch
{
    /// <summary>環境検出で見つかっている必要があるツール名（例: podman）。</summary>
    public required string Tool { get; init; }

    /// <summary>/etc/os-release のIDまたはID_LIKEのいずれかと一致すれば対象。"*" は全OS。</summary>
    public string[] OsIds { get; init; } = ["*"];

    /// <summary>ツールの最低バージョン（含む）。</summary>
    public string? MinToolVersion { get; init; }

    /// <summary>ツールの上限バージョン（含まない）。旧バージョン専用テンプレートに使う。</summary>
    public string? MaxToolVersion { get; init; }
}

public sealed partial record CommandDefinition
{
    /// <summary>引数。{name} はプレースホルダで、実行時に値へ置換される。</summary>
    public required string[] Args { get; init; }

    /// <summary>出力を解析するパーサー名（一覧取得系コマンドのみ）。</summary>
    public string? Parser { get; init; }

    public int TimeoutSeconds { get; init; } = 60;

    public IReadOnlyList<string> Render(IReadOnlyDictionary<string, string> values) =>
        Args.Select(arg => Placeholder().Replace(arg, m => values.TryGetValue(m.Groups[1].Value, out var value)
            ? value
            : throw new InvalidOperationException($"プレースホルダ {{{m.Groups[1].Value}}} の値がありません。"))).ToList();

    public IEnumerable<string> Placeholders => Args.SelectMany(a => Placeholder().Matches(a).Select(m => m.Groups[1].Value));

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex Placeholder();
}
