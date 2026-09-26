namespace SystemAgent.Core.CapabilityProviders;

/// <summary>
/// NTP（時刻同期）設定を抽象化するインターフェース。
/// </summary>
public interface INtpProvider
{
    Task<NtpSyncStatus> GetSyncStatusAsync(CancellationToken cancellationToken = default);

    Task SetNtpServerAsync(IReadOnlyList<string> servers, CancellationToken cancellationToken = default);
}

public sealed record NtpSyncStatus(bool IsSynchronized, IReadOnlyList<string> Servers);
