using System.Text.RegularExpressions;
using SystemAgent.Core.CapabilityProviders;
using SystemAgent.Infrastructure.CapabilityProviders.Templates;

namespace SystemAgent.Infrastructure.CapabilityProviders.Containers;

/// <summary>
/// コマンドテンプレートに従ってコンテナランタイムを操作する。Podman/Dockerとも実装はこの1クラスで、
/// 差分はテンプレート(JSON)とパーサーに閉じている（ADR-005）。
/// </summary>
public sealed partial class TemplateContainerRuntimeProvider(TemplateCommandExecutor executor, RuntimeInfo runtime)
    : IContainerRuntimeProvider
{
    public RuntimeInfo Runtime => runtime;

    public bool SupportsPods => executor.Has("listPods");

    public async Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(CancellationToken cancellationToken = default) =>
        ContainerOutputParsers.Containers[executor.Parser("listContainers")!](
            (await executor.RunAsync("listContainers", null, cancellationToken)).StandardOutput);

    public Task StartContainerAsync(string id, CancellationToken cancellationToken = default) =>
        executor.RunAsync("startContainer", Values(("id", Reference(id))), cancellationToken);

    public Task StopContainerAsync(string id, CancellationToken cancellationToken = default) =>
        executor.RunAsync("stopContainer", Values(("id", Reference(id))), cancellationToken);

    public Task RestartContainerAsync(string id, CancellationToken cancellationToken = default) =>
        executor.RunAsync("restartContainer", Values(("id", Reference(id))), cancellationToken);

    public Task RemoveContainerAsync(string id, CancellationToken cancellationToken = default) =>
        executor.RunAsync("removeContainer", Values(("id", Reference(id))), cancellationToken);

    public async Task<string> GetContainerLogsAsync(string id, int tail, CancellationToken cancellationToken = default)
    {
        if (tail is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(tail), "tailは1〜10000で指定してください。");
        // コンテナの標準エラー出力はランタイムの標準エラーに出るため、両方を返す
        var result = await executor.RunAsync("containerLogs", Values(("id", Reference(id)), ("tail", tail.ToString())), cancellationToken);
        return result.StandardOutput + result.StandardError;
    }

    public async Task<IReadOnlyList<ImageInfo>> ListImagesAsync(CancellationToken cancellationToken = default) =>
        ContainerOutputParsers.Images[executor.Parser("listImages")!](
            (await executor.RunAsync("listImages", null, cancellationToken)).StandardOutput);

    public Task PullImageAsync(string image, CancellationToken cancellationToken = default) =>
        executor.RunAsync("pullImage", Values(("image", Reference(image))), cancellationToken);

    public Task RemoveImageAsync(string id, CancellationToken cancellationToken = default) =>
        executor.RunAsync("removeImage", Values(("id", Reference(id))), cancellationToken);

    public async Task<string> LoadImageAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        // 標準出力に "Loaded image: ..."、標準エラーに進捗が出る（podman/docker共通）
        var result = await executor.RunAsync("loadImage", Values(("file", archivePath)), cancellationToken);
        return result.StandardOutput.Trim() is { Length: > 0 } loaded ? loaded : result.StandardError.Trim();
    }

    public async Task<IReadOnlyList<PodInfo>> ListPodsAsync(CancellationToken cancellationToken = default)
    {
        if (!SupportsPods) return [];
        return ContainerOutputParsers.Pods[executor.Parser("listPods")!](
            (await executor.RunAsync("listPods", null, cancellationToken)).StandardOutput);
    }

    public Task LoginAsync(string registry, string username, string password, CancellationToken cancellationToken = default)
    {
        RequireCommand("registryLogin");
        if (username.Length is 0 or > 256 || username.StartsWith('-') || username.Any(char.IsControl))
            throw new ArgumentException("ユーザー名が不正です。");
        return executor.RunWithInputAsync("registryLogin",
            Values(("registry", RegistryName.Validate(registry)), ("username", username)), password, cancellationToken);
    }

    public Task LogoutAsync(string registry, CancellationToken cancellationToken = default)
    {
        RequireCommand("registryLogout");
        return executor.RunAsync("registryLogout", Values(("registry", RegistryName.Validate(registry))), cancellationToken);
    }

    private void RequireCommand(string command)
    {
        if (!executor.Has(command))
            throw new CapabilityUnavailableException($"テンプレート {executor.Template.Id} はレジストリ操作（{command}）に対応していません。");
    }

    private static Dictionary<string, string> Values(params (string Key, string Value)[] values) =>
        values.ToDictionary(v => v.Key, v => v.Value);

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
