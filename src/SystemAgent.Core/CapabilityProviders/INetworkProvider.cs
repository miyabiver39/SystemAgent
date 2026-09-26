namespace SystemAgent.Core.CapabilityProviders;

/// <summary>
/// ホストネットワークを抽象化するインターフェース（基本設計書 2.1節）。
/// 現在は参照のみ。設定変更は安全策（自動ロールバック等）の方針確定後に追加する（QA 0005）。
/// </summary>
public interface INetworkProvider
{
    Task<NetworkStatus> GetStatusAsync(CancellationToken cancellationToken = default);
}

public sealed record NetworkStatus(
    IReadOnlyList<NetworkInterfaceInfo> Interfaces,
    IReadOnlyList<RouteInfo> Routes,
    IReadOnlyList<string> DnsServers,
    IReadOnlyList<string> SearchDomains);

public enum InterfaceKind
{
    Other,
    Loopback,
    Physical,
    Bridge,
    Bond,
    Vlan,
    Veth,
    Dummy,
    Tunnel,
}

/// <param name="Master">ブリッジ/ボンドの配下にある場合の親インターフェース。</param>
/// <param name="ContainerNetwork">コンテナランタイムが作ったもの（podman0, docker0, veth等）。ホスト設定の対象外。</param>
public sealed record NetworkInterfaceInfo(
    string Name,
    InterfaceKind Kind,
    string? MacAddress,
    string State,
    int? Mtu,
    string? Master,
    bool ContainerNetwork,
    IReadOnlyList<InterfaceAddress> Addresses);

/// <param name="Dynamic">DHCP等で動的に割り当てられたか。</param>
public sealed record InterfaceAddress(string Family, string Address, int PrefixLength, bool Dynamic)
{
    public override string ToString() => $"{Address}/{PrefixLength}";
}

public sealed record RouteInfo(string Family, string Destination, string? Gateway, string? Device, string? Protocol, int? Metric);
