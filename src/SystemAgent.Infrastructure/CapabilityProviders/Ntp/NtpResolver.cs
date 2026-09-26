using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SystemAgent.Core.CapabilityProviders;
using SystemAgent.Infrastructure.CapabilityProviders.Templates;
using SystemAgent.Infrastructure.Commands;

namespace SystemAgent.Infrastructure.CapabilityProviders.Ntp;

/// <summary>設定(Ntp:Implementation = auto|chronyc|timesyncd)と環境検出からNTP実装を決める。autoはchrony優先。</summary>
public sealed class NtpResolver(
    IEnvironmentDetector detector, CommandTemplateStore templates, ICommandRunner runner,
    IConfiguration configuration, ILogger<TemplateNtpProvider> logger)
{
    public const string Capability = "ntp";
    private static readonly string[] AutoOrder = ["chronyc", "timesyncd"];

    public async Task<INtpProvider> ResolveAsync(CancellationToken cancellationToken = default)
    {
        var environment = await detector.DetectAsync(cancellationToken: cancellationToken);
        var configured = configuration["Ntp:Implementation"] ?? "auto";
        var candidates = configured == "auto" ? AutoOrder : [configured];

        var tool = candidates.FirstOrDefault(c => environment.FindTool(c) is not null)
            ?? throw new CapabilityUnavailableException("時刻同期サービス（chrony / systemd-timesyncd）が見つかりません。");

        var template = templates.Resolve(Capability, tool, environment)
            ?? throw new CapabilityUnavailableException(
                $"{tool}（{environment.OsId} {environment.OsVersion}）に対応するコマンドテンプレートがありません。");

        return new TemplateNtpProvider(new TemplateCommandExecutor(template, runner), logger);
    }
}
