using Microsoft.Extensions.Configuration;
using SystemAgent.Core.CapabilityProviders;
using SystemAgent.Infrastructure.CapabilityProviders.Templates;

namespace SystemAgent.Infrastructure.CapabilityProviders.Services;

/// <summary>
/// 管理対象サービスの一覧と操作の可否（ADR-021）。
/// 操作（起動・停止等）できるのは Services:Managed に列挙したサービスのみ。
/// SystemAgent自身とsshdは、停止・無効化するとリモートから操作できなくなるため停止系の操作を拒否する。
/// </summary>
public sealed class ServiceManagement(CapabilityTemplateResolver resolver, IConfiguration configuration)
{
    public static readonly string[] DefaultManaged =
    [
        "chronyd", "chrony", "systemd-timesyncd", "keepalived", "mariadb", "mysqld", "podman", "podman.socket", "podman-restart", "docker",
        "NetworkManager", "systemd-networkd", "nginx", "sshd", "ssh",
    ];

    private static readonly string[] Protected = ["systemagent", "sshd", "ssh"];

    public IReadOnlyList<string> Managed =>
        configuration.GetSection("Services:Managed").Get<string[]>() is { Length: > 0 } list ? list : DefaultManaged;

    public async Task<IServiceManagerProvider> ResolveAsync(CancellationToken cancellationToken)
    {
        var (executor, _) = await resolver.ResolveAsync("service-manager", "Services:Tool", ["systemctl"], "サービス管理（systemd）", cancellationToken);
        return new TemplateServiceManagerProvider(executor);
    }

    /// <summary>状態の取得（systemctl の起動）を同時に行う最大数。</summary>
    public const int MaxParallelStatus = 4;

    /// <summary>
    /// 管理対象サービスのうち、このノードに存在するものの状態（設定の順）。1件ずつ systemctl を起動すると件数分待つため、
    /// 同時に MaxParallelStatus 件まで並行して取得する。別名（例: mysqld → mariadb）で同じサービスが重複しないようにする。
    /// </summary>
    public async Task<IReadOnlyList<ServiceStatus>> ListExistingAsync(IServiceManagerProvider provider, CancellationToken cancellationToken)
    {
        using var throttle = new SemaphoreSlim(MaxParallelStatus);
        var statuses = await Task.WhenAll(Managed.Select(async unit =>
        {
            await throttle.WaitAsync(cancellationToken);
            try
            {
                return await provider.GetStatusAsync(unit, cancellationToken);
            }
            finally
            {
                throttle.Release();
            }
        }));
        return statuses.Where(s => s.Exists).DistinctBy(s => s.Name).ToList();
    }

    /// <summary>操作できない場合は理由を返す。</summary>
    public string? Deny(string unit, ServiceAction action)
    {
        var name = unit.EndsWith(".service", StringComparison.Ordinal) ? unit[..^".service".Length] : unit;
        if (!Managed.Contains(name) && !Managed.Contains(unit))
            return $"'{unit}' は管理対象のサービスではありません（Services:Managed に追加すると操作できます）。";
        if (action is ServiceAction.Stop or ServiceAction.Disable && Protected.Contains(name))
            return $"'{unit}' を停止・無効化するとリモートから操作できなくなるため、SystemAgentからは実行できません。";
        return null;
    }
}
