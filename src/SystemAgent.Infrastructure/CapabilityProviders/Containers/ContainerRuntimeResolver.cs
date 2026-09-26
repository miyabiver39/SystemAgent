using Microsoft.Extensions.Configuration;
using SystemAgent.Core.CapabilityProviders;
using SystemAgent.Infrastructure.CapabilityProviders.Templates;
using SystemAgent.Infrastructure.Commands;

namespace SystemAgent.Infrastructure.CapabilityProviders.Containers;

/// <summary>
/// 環境検出結果と設定(Container:Runtime = auto|podman|docker)から、使うランタイムとテンプレートを決める。
/// auto はPodman優先（QA 0004）。
/// </summary>
public sealed class ContainerRuntimeResolver(
    IEnvironmentDetector detector, CommandTemplateStore templates, ICommandRunner runner, IConfiguration configuration)
{
    public const string Capability = "container-runtime";
    private static readonly string[] AutoOrder = ["podman", "docker"];

    public async Task<IContainerRuntimeProvider> ResolveAsync(CancellationToken cancellationToken = default)
    {
        var environment = await detector.DetectAsync(cancellationToken: cancellationToken);
        var configured = configuration["Container:Runtime"] ?? "auto";
        var candidates = configured == "auto" ? AutoOrder : [configured];

        var tool = candidates.FirstOrDefault(c => environment.FindTool(c) is not null)
            ?? throw new CapabilityUnavailableException(configured == "auto"
                ? "コンテナランタイム（podman / docker）が見つかりません。"
                : $"設定されたコンテナランタイム '{configured}' が見つかりません。");

        var toolInfo = environment.FindTool(tool)!;
        var template = templates.Resolve(Capability, tool, environment)
            ?? throw new CapabilityUnavailableException(
                $"{tool} {toolInfo.Version}（{environment.OsId} {environment.OsVersion}）に対応するコマンドテンプレートがありません。");

        return new TemplateContainerRuntimeProvider(
            new TemplateCommandExecutor(template, runner), new RuntimeInfo(tool, toolInfo.Version, template.Id));
    }
}
