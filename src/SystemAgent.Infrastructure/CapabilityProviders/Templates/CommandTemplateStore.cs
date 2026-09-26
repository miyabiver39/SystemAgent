using System.Text.Json;
using Microsoft.Extensions.Logging;
using SystemAgent.Core.CapabilityProviders;

namespace SystemAgent.Infrastructure.CapabilityProviders.Templates;

/// <summary>
/// コマンドテンプレートを読み込み、実行環境に最も適合するものを選ぶ。
/// 同梱テンプレート（不正なら起動失敗）に加え、追加ディレクトリ（CommandTemplates:ExtraPath）のテンプレートで
/// 同じIdを上書き・追加できる。追加ディレクトリの不正なテンプレートはエラーログを出して無視する。
/// </summary>
public sealed class CommandTemplateStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public IReadOnlyList<CommandTemplate> Templates { get; }

    public CommandTemplateStore(string builtInDirectory, string? extraDirectory, ILogger<CommandTemplateStore> logger)
    {
        var templates = new Dictionary<string, CommandTemplate>();
        foreach (var template in LoadDirectory(builtInDirectory, logger, strict: true)) templates[template.Id] = template;
        if (!string.IsNullOrEmpty(extraDirectory) && Directory.Exists(extraDirectory))
        {
            foreach (var template in LoadDirectory(extraDirectory, logger, strict: false))
            {
                logger.LogInformation("追加テンプレート {Id} を読み込みました: {Path}", template.Id, template.SourcePath);
                templates[template.Id] = template;
            }
        }
        Templates = templates.Values.ToList();
    }

    /// <summary>
    /// 条件（capability・ツール・バージョン・OS）を満たすテンプレートのうち、OS指定が最も具体的で
    /// 最低バージョンが最も高いものを返す。
    /// </summary>
    public CommandTemplate? Resolve(string capability, string tool, HostEnvironment environment)
    {
        var toolInfo = environment.FindTool(tool);
        if (toolInfo is null) return null;
        var toolVersion = VersionText.Parse(toolInfo.Version);

        return Templates
            .Where(t => t.Capability == capability && t.Match.Tool == tool)
            .Where(t => t.Match.MinToolVersion is null || toolVersion >= VersionText.Parse(t.Match.MinToolVersion))
            .Where(t => t.Match.MaxToolVersion is null || toolVersion < VersionText.Parse(t.Match.MaxToolVersion))
            .Select(t => (Template: t, OsScore: OsScore(t.Match.OsIds, environment)))
            .Where(x => x.OsScore > 0)
            .OrderByDescending(x => x.OsScore)
            .ThenByDescending(x => VersionText.Parse(x.Template.Match.MinToolVersion ?? "0"))
            .Select(x => x.Template)
            .FirstOrDefault();
    }

    private static int OsScore(string[] osIds, HostEnvironment environment)
    {
        if (osIds.Contains(environment.OsId)) return 3;
        if (environment.OsIdLike.Any(osIds.Contains)) return 2;
        return osIds.Contains("*") ? 1 : 0;
    }

    private static IEnumerable<CommandTemplate> LoadDirectory(string directory, ILogger logger, bool strict)
    {
        foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories)
                     .Where(p => !p.EndsWith(".schema.json", StringComparison.OrdinalIgnoreCase))
                     .Order())
        {
            CommandTemplate? template;
            IReadOnlyList<string> errors;
            try
            {
                template = JsonSerializer.Deserialize<CommandTemplate>(File.ReadAllText(path), Json)! with { SourcePath = path };
                errors = TemplateRequirements.Validate(template);
            }
            catch (JsonException ex)
            {
                template = null;
                errors = [$"JSONとして読み込めません: {ex.Message}"];
            }

            if (errors.Count == 0)
            {
                yield return template!;
                continue;
            }

            var message = $"コマンドテンプレート {path} が不正です: {string.Join(" / ", errors)}";
            if (strict) throw new InvalidOperationException(message);
            logger.LogError("{Message}", message);
        }
    }
}

public static class VersionText
{
    /// <summary>"5.8.2" や "v2.2.8"、"252" などからVersionを得る。数字が無ければ0.0。</summary>
    public static Version Parse(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, @"\d+(\.\d+){0,3}");
        if (!match.Success) return new Version(0, 0);
        var value = match.Value.Contains('.') ? match.Value : match.Value + ".0";
        return Version.Parse(value);
    }
}
