using System.Text.RegularExpressions;
using SystemAgent.Core.CapabilityProviders;
using SystemAgent.Infrastructure.CapabilityProviders.Templates;
using SystemAgent.Infrastructure.Commands;

namespace SystemAgent.Infrastructure.CapabilityProviders.Containers;

/// <summary>
/// コマンドテンプレートに従ってコンテナランタイムを操作する。Podman/Dockerとも実装はこの1クラスで、
/// 差分はテンプレート(JSON)とパーサーに閉じている（ADR-005）。
/// </summary>
public sealed partial class TemplateContainerRuntimeProvider(
    CommandTemplate template, RuntimeInfo runtime, ICommandRunner runner) : IContainerRuntimeProvider
{
    public RuntimeInfo Runtime => runtime;

    public bool SupportsPods => template.Commands.ContainsKey("listPods");

    public async Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(CancellationToken cancellationToken = default)
    {
        var (definition, output) = await RunAsync("listContainers", [], cancellationToken);
        return ContainerOutputParsers.Containers[definition.Parser!](output);
    }

    public Task StartContainerAsync(string id, CancellationToken cancellationToken = default) =>
        RunAsync("startContainer", [("id", Reference(id))], cancellationToken);

    public Task StopContainerAsync(string id, CancellationToken cancellationToken = default) =>
        RunAsync("stopContainer", [("id", Reference(id))], cancellationToken);

    public Task RestartContainerAsync(string id, CancellationToken cancellationToken = default) =>
        RunAsync("restartContainer", [("id", Reference(id))], cancellationToken);

    public Task RemoveContainerAsync(string id, CancellationToken cancellationToken = default) =>
        RunAsync("removeContainer", [("id", Reference(id))], cancellationToken);

    public async Task<string> GetContainerLogsAsync(string id, int tail, CancellationToken cancellationToken = default)
    {
        if (tail is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(tail), "tailは1〜10000で指定してください。");
        // コンテナの標準エラー出力はランタイムの標準エラーに出るため、両方を返す
        var result = await RunRawAsync("containerLogs", [("id", Reference(id)), ("tail", tail.ToString())], cancellationToken);
        return result.StandardOutput + result.StandardError;
    }

    public async Task<IReadOnlyList<ImageInfo>> ListImagesAsync(CancellationToken cancellationToken = default)
    {
        var (definition, output) = await RunAsync("listImages", [], cancellationToken);
        return ContainerOutputParsers.Images[definition.Parser!](output);
    }

    public Task PullImageAsync(string image, CancellationToken cancellationToken = default) =>
        RunAsync("pullImage", [("image", Reference(image))], cancellationToken);

    public Task RemoveImageAsync(string id, CancellationToken cancellationToken = default) =>
        RunAsync("removeImage", [("id", Reference(id))], cancellationToken);

    public async Task<string> LoadImageAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        // 標準出力に "Loaded image: ..."、標準エラーに進捗が出る（podman/docker共通）
        var result = await RunRawAsync("loadImage", [("file", archivePath)], cancellationToken);
        return result.StandardOutput.Trim() is { Length: > 0 } loaded ? loaded : result.StandardError.Trim();
    }

    public async Task<IReadOnlyList<PodInfo>> ListPodsAsync(CancellationToken cancellationToken = default)
    {
        if (!SupportsPods) return [];
        var (definition, output) = await RunAsync("listPods", [], cancellationToken);
        return ContainerOutputParsers.Pods[definition.Parser!](output);
    }

    private async Task<(CommandDefinition Definition, string Output)> RunAsync(
        string command, (string Key, string Value)[] values, CancellationToken cancellationToken)
    {
        var result = await RunRawAsync(command, values, cancellationToken);
        return (template.Commands[command], result.StandardOutput);
    }

    private async Task<CommandResult> RunRawAsync(string command, (string Key, string Value)[] values, CancellationToken cancellationToken)
    {
        var definition = template.Commands[command];
        var args = definition.Render(values.ToDictionary(v => v.Key, v => v.Value));
        var result = await runner.RunAsync(template.Executable, args, TimeSpan.FromSeconds(definition.TimeoutSeconds), cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new CommandFailedException($"{template.Executable} {string.Join(' ', args)}", result.ExitCode, result.StandardError);
        }
        return result;
    }

    /// <summary>
    /// コンテナID/名前/イメージ参照として妥当か検証する。先頭の '-' を許すとオプションとして解釈されるため拒否する。
    /// </summary>
    private static string Reference(string value) =>
        value.Length <= 512 && ReferencePattern().IsMatch(value)
            ? value
            : throw new ArgumentException($"コンテナ/イメージの指定が不正です: {value}");

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.:/@+-]*$")]
    private static partial Regex ReferencePattern();
}
