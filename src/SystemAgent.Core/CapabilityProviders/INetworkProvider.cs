namespace SystemAgent.Core.CapabilityProviders;

/// <summary>
/// ホストネットワーク設定を抽象化するインターフェース。
/// </summary>
public interface INetworkProvider
{
    Task<IReadOnlyList<NetworkInterfaceInfo>> GetInterfacesAsync(CancellationToken cancellationToken = default);

    Task SetIpAddressAsync(string interfaceName, string ipAddress, string prefix, CancellationToken cancellationToken = default);
}

public sealed record NetworkInterfaceInfo(string Name, string? IpAddress, bool IsUp);
