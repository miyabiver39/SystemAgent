namespace SystemAgent.Core.CapabilityProviders;

/// <summary>
/// サービス管理（systemd）を抽象化するインターフェース（基本設計書 7章）。
/// </summary>
public interface IServiceManagerProvider
{
    Task<ServiceStatus> GetStatusAsync(string unit, CancellationToken cancellationToken = default);

    /// <param name="action">start / stop / restart / enable / disable</param>
    Task ExecuteAsync(string unit, ServiceAction action, CancellationToken cancellationToken = default);

    Task<string> GetLogsAsync(string unit, int lines, CancellationToken cancellationToken = default);
}

public enum ServiceAction
{
    Start,
    Stop,
    Restart,
    Enable,
    Disable,
}

/// <param name="Exists">ユニットが存在するか（LoadState が not-found でない）。</param>
/// <param name="ActiveState">active / inactive / failed / activating 等。</param>
/// <param name="UnitFileState">enabled / disabled / masked / static 等（自動起動の設定）。</param>
public sealed record ServiceStatus(
    string Name,
    string Description,
    bool Exists,
    string LoadState,
    string ActiveState,
    string SubState,
    string UnitFileState,
    int? MainPid,
    DateTimeOffset? ActiveSince);
