namespace SystemAgent.Core.CapabilityProviders;

/// <summary>
/// systemd等のサービス管理を抽象化するインターフェース。
/// </summary>
public interface IServiceManagerProvider
{
    Task StartServiceAsync(string serviceName, CancellationToken cancellationToken = default);

    Task StopServiceAsync(string serviceName, CancellationToken cancellationToken = default);

    Task<ServiceStatus> GetStatusAsync(string serviceName, CancellationToken cancellationToken = default);
}

public sealed record ServiceStatus(string Name, bool IsActive, bool IsEnabled);
