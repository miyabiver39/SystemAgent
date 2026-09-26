using SystemAgent.Infrastructure.CapabilityProviders.Containers;

namespace SystemAgent.Infrastructure.CapabilityProviders.Templates;

/// <summary>
/// Capabilityごとにテンプレートが定義すべきコマンドと、使えるプレースホルダ・パーサー。
/// 新しいCapabilityを追加するときはここに要件を追加する（テンプレート読み込み時に検証される）。
/// </summary>
public static class TemplateRequirements
{
    public sealed record CommandRequirement(bool Required, string[] Placeholders, IEnumerable<string>? Parsers = null);

    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, CommandRequirement>> ByCapability =
        new Dictionary<string, IReadOnlyDictionary<string, CommandRequirement>>
        {
            ["container-runtime"] = new Dictionary<string, CommandRequirement>
            {
                ["listContainers"] = new(true, [], ContainerOutputParsers.Containers.Keys),
                ["startContainer"] = new(true, ["id"]),
                ["stopContainer"] = new(true, ["id"]),
                ["restartContainer"] = new(true, ["id"]),
                ["removeContainer"] = new(true, ["id"]),
                ["containerLogs"] = new(true, ["id", "tail"]),
                ["listImages"] = new(true, [], ContainerOutputParsers.Images.Keys),
                ["pullImage"] = new(true, ["image"]),
                ["removeImage"] = new(true, ["id"]),
                ["loadImage"] = new(true, ["file"]),
                ["listPods"] = new(false, [], ContainerOutputParsers.Pods.Keys),
            },
        };

    /// <returns>問題の一覧（空なら妥当）。</returns>
    public static IReadOnlyList<string> Validate(CommandTemplate template)
    {
        var errors = new List<string>();
        if (!ByCapability.TryGetValue(template.Capability, out var requirements))
        {
            errors.Add($"未知のcapability '{template.Capability}'");
            return errors;
        }

        foreach (var (name, requirement) in requirements)
        {
            if (!template.Commands.ContainsKey(name) && requirement.Required) errors.Add($"必須コマンド '{name}' がありません");
        }

        foreach (var (name, command) in template.Commands)
        {
            if (!requirements.TryGetValue(name, out var requirement))
            {
                errors.Add($"'{name}' は {template.Capability} のコマンドではありません");
                continue;
            }
            foreach (var placeholder in command.Placeholders.Except(requirement.Placeholders))
            {
                errors.Add($"'{name}' で使えないプレースホルダ {{{placeholder}}}（使用可能: {string.Join(", ", requirement.Placeholders)}）");
            }
            if (requirement.Parsers is { } parsers && (command.Parser is null || !parsers.Contains(command.Parser)))
            {
                errors.Add($"'{name}' のparserは次のいずれかを指定してください: {string.Join(", ", parsers)}");
            }
            if (command.TimeoutSeconds <= 0) errors.Add($"'{name}' のtimeoutSecondsは正の値にしてください");
        }
        return errors;
    }
}
