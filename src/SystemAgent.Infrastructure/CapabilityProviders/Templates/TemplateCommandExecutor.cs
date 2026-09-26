using SystemAgent.Core.CapabilityProviders;
using SystemAgent.Infrastructure.Commands;

namespace SystemAgent.Infrastructure.CapabilityProviders.Templates;

/// <summary>
/// テンプレートのコマンドを実行する共通処理。プレースホルダにはテンプレートのsettingsと呼び出し側の値を使う。
/// 終了コードが0以外ならCommandFailedException。
/// </summary>
public sealed class TemplateCommandExecutor(CommandTemplate template, ICommandRunner runner)
{
    public CommandTemplate Template => template;

    public bool Has(string command) => template.Commands.ContainsKey(command);

    public async Task<CommandResult> RunAsync(
        string command, IReadOnlyDictionary<string, string>? values = null, CancellationToken cancellationToken = default)
    {
        var definition = template.Commands[command];
        var merged = new Dictionary<string, string>(template.Settings);
        foreach (var (key, value) in values ?? new Dictionary<string, string>()) merged[key] = value;

        var executable = definition.Executable ?? template.Executable;
        var args = definition.Render(merged);
        var result = await runner.RunAsync(executable, args, TimeSpan.FromSeconds(definition.TimeoutSeconds), cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new CommandFailedException($"{executable} {string.Join(' ', args)}", result.ExitCode, result.StandardError);
        }
        return result;
    }

    public string? Parser(string command) => template.Commands[command].Parser;

    public string Setting(string key) => template.Settings[key];
}
