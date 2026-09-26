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
        var (executable, args, timeout) = Prepare(command, values);
        return EnsureSuccess(executable, args, await runner.RunAsync(executable, args, timeout, cancellationToken));
    }

    /// <summary>
    /// 標準入力に値を渡して実行する（パスワード等をコマンドライン引数に載せないため）。
    /// 戻り値のStandardOutputは空。
    /// </summary>
    public async Task<CommandResult> RunWithInputAsync(
        string command, IReadOnlyDictionary<string, string>? values, string input, CancellationToken cancellationToken = default)
    {
        var (executable, args, timeout) = Prepare(command, values);
        using var stdin = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(input));
        return EnsureSuccess(executable, args, await runner.RunStreamingAsync(executable, args, stdin, null, timeout, cancellationToken));
    }

    private (string Executable, IReadOnlyList<string> Args, TimeSpan Timeout) Prepare(string command, IReadOnlyDictionary<string, string>? values)
    {
        var definition = template.Commands[command];
        var merged = new Dictionary<string, string>(template.Settings);
        foreach (var (key, value) in values ?? new Dictionary<string, string>()) merged[key] = value;
        return (definition.Executable ?? template.Executable, definition.Render(merged), TimeSpan.FromSeconds(definition.TimeoutSeconds));
    }

    private static CommandResult EnsureSuccess(string executable, IReadOnlyList<string> args, CommandResult result) =>
        result.ExitCode == 0
            ? result
            : throw new CommandFailedException($"{executable} {string.Join(' ', args)}", result.ExitCode, result.StandardError);

    public string? Parser(string command) => template.Commands[command].Parser;

    public string Setting(string key) => template.Settings[key];
}
