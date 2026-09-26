using Microsoft.Extensions.Configuration;
using SystemAgent.Core.CapabilityProviders;
using SystemAgent.Infrastructure.Commands;

namespace SystemAgent.Infrastructure.CapabilityProviders.Templates;

/// <summary>
/// 環境検出結果と設定から、Capabilityに使うツールとテンプレートを決める共通処理。
/// 設定キー（例: Ntp:Implementation）が "auto" または未設定なら候補を順に試す。
/// </summary>
public sealed class CapabilityTemplateResolver(
    IEnvironmentDetector detector, CommandTemplateStore templates, ICommandRunner runner, IConfiguration configuration)
{
    public sealed record Resolution(TemplateCommandExecutor Executor, ToolInfo Tool);

    public async Task<Resolution> ResolveAsync(
        string capability, string configurationKey, IReadOnlyList<string> autoCandidates, string displayName,
        CancellationToken cancellationToken)
    {
        var environment = await detector.DetectAsync(cancellationToken: cancellationToken);
        var configured = configuration[configurationKey] ?? "auto";
        var candidates = configured == "auto" ? autoCandidates : [configured];

        var tool = candidates.Select(environment.FindTool).FirstOrDefault(t => t is not null)
            ?? throw new CapabilityUnavailableException(configured == "auto"
                ? $"{displayName}（{string.Join(" / ", autoCandidates)}）が見つかりません。"
                : $"設定された{displayName} '{configured}'（{configurationKey}）が見つかりません。");

        var template = templates.Resolve(capability, tool.Name, environment)
            ?? throw new CapabilityUnavailableException(
                $"{tool.Name} {tool.Version}（{environment.OsId} {environment.OsVersion}）に対応するコマンドテンプレートがありません。");

        return new Resolution(new TemplateCommandExecutor(template, runner), tool);
    }
}
