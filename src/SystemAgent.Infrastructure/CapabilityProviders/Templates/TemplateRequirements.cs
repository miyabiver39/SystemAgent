using SystemAgent.Infrastructure.CapabilityProviders.Containers;
using SystemAgent.Infrastructure.CapabilityProviders.Network;
using SystemAgent.Infrastructure.CapabilityProviders.Ntp;
using SystemAgent.Infrastructure.CapabilityProviders.Services;

namespace SystemAgent.Infrastructure.CapabilityProviders.Templates;

/// <summary>
/// Capabilityごとにテンプレートが定義すべきコマンド・設定値と、使えるプレースホルダ・パーサー。
/// 新しいCapabilityを追加するときはここに要件を追加する（テンプレート読み込み時に検証される）。
/// </summary>
public static class TemplateRequirements
{
    public sealed record CommandRequirement(bool Required, string[] Placeholders, IEnumerable<string>? Parsers = null);

    /// <param name="AllowedValues">指定時は値がこの中のいずれかである必要がある。</param>
    public sealed record SettingRequirement(IEnumerable<string>? AllowedValues = null);

    public sealed record CapabilityRequirement(
        IReadOnlyDictionary<string, CommandRequirement> Commands,
        IReadOnlyDictionary<string, SettingRequirement> Settings);

    public static readonly IReadOnlyDictionary<string, CapabilityRequirement> ByCapability =
        new Dictionary<string, CapabilityRequirement>
        {
            ["container-runtime"] = new(
                Commands: new Dictionary<string, CommandRequirement>
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
                Settings: new Dictionary<string, SettingRequirement>()),

            ["ntp"] = new(
                Commands: new Dictionary<string, CommandRequirement>
                {
                    ["status"] = new(true, [], NtpOutputParsers.Status.Keys),
                    ["sources"] = new(false, [], NtpOutputParsers.Sources.Keys),
                    // statusで同期状態が取れない実装用。出力が "yes" なら同期済み
                    ["synchronized"] = new(false, []),
                    ["restartService"] = new(true, ["service"]),
                    ["syncNow"] = new(true, ["service"]),
                },
                Settings: new Dictionary<string, SettingRequirement>
                {
                    ["implementation"] = new(),
                    ["configFile"] = new(),
                    ["configStyle"] = new(NtpConfigWriters.ByStyle.Keys),
                    ["service"] = new(),
                }),

            ["db-backup"] = new(
                Commands: new Dictionary<string, CommandRequirement>
                {
                    // 標準出力にSQLを出す（BackupServiceが速度制限しながら圧縮・保存する）
                    ["dump"] = new(true, ["defaults", "database"]),
                    // 標準入力からSQLを読む
                    ["restore"] = new(true, ["defaults", "database"]),
                },
                Settings: new Dictionary<string, SettingRequirement>()),

            ["service-manager"] = new(
                Commands: new Dictionary<string, CommandRequirement>
                {
                    ["status"] = new(true, ["unit"], ServiceOutputParsers.Status.Keys),
                    // ServiceAction の小文字名と対応する
                    ["start"] = new(true, ["unit"]),
                    ["stop"] = new(true, ["unit"]),
                    ["restart"] = new(true, ["unit"]),
                    ["enable"] = new(true, ["unit"]),
                    ["disable"] = new(true, ["unit"]),
                    ["logs"] = new(true, ["unit", "lines"]),
                },
                Settings: new Dictionary<string, SettingRequirement>()),

            ["network"] = new(
                Commands: new Dictionary<string, CommandRequirement>
                {
                    ["listInterfaces"] = new(true, [], NetworkOutputParsers.Interfaces.Keys),
                    ["listRoutes"] = new(true, [], NetworkOutputParsers.Routes.Keys),
                    ["listRoutes6"] = new(false, [], NetworkOutputParsers.Routes.Keys),
                },
                Settings: new Dictionary<string, SettingRequirement>
                {
                    ["resolvConf"] = new(),
                }),
        };

    /// <returns>問題の一覧（空なら妥当）。</returns>
    public static IReadOnlyList<string> Validate(CommandTemplate template)
    {
        var errors = new List<string>();
        if (!ByCapability.TryGetValue(template.Capability, out var requirement))
        {
            errors.Add($"未知のcapability '{template.Capability}'");
            return errors;
        }

        foreach (var (name, setting) in requirement.Settings)
        {
            if (!template.Settings.TryGetValue(name, out var value) || value.Length == 0)
                errors.Add($"必須設定 settings.{name} がありません");
            else if (setting.AllowedValues is { } allowed && !allowed.Contains(value))
                errors.Add($"settings.{name} は次のいずれかを指定してください: {string.Join(", ", allowed)}");
        }

        foreach (var (name, command) in requirement.Commands)
        {
            if (!template.Commands.ContainsKey(name) && command.Required) errors.Add($"必須コマンド '{name}' がありません");
        }

        foreach (var (name, command) in template.Commands)
        {
            if (!requirement.Commands.TryGetValue(name, out var commandRequirement))
            {
                errors.Add($"'{name}' は {template.Capability} のコマンドではありません");
                continue;
            }
            foreach (var placeholder in command.Placeholders.Except(commandRequirement.Placeholders))
            {
                errors.Add($"'{name}' で使えないプレースホルダ {{{placeholder}}}（使用可能: {string.Join(", ", commandRequirement.Placeholders)}）");
            }
            if (commandRequirement.Parsers is { } parsers && (command.Parser is null || !parsers.Contains(command.Parser)))
            {
                errors.Add($"'{name}' のparserは次のいずれかを指定してください: {string.Join(", ", parsers)}");
            }
            if (command.TimeoutSeconds <= 0) errors.Add($"'{name}' のtimeoutSecondsは正の値にしてください");
        }
        return errors;
    }
}
