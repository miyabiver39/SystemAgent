using System.Text.Json;
using SystemAgent.Core.CapabilityProviders;

namespace SystemAgent.Infrastructure.CapabilityProviders.Network;

/// <summary>ネットワーク情報の出力パーサー。テンプレートの "parser" から名前で参照される。</summary>
public static class NetworkOutputParsers
{
    public static readonly IReadOnlyDictionary<string, Func<string, IReadOnlyList<NetworkInterfaceInfo>>> Interfaces =
        new Dictionary<string, Func<string, IReadOnlyList<NetworkInterfaceInfo>>>
        {
            ["iproute2-addr-json"] = ParseIpAddr,
        };

    public static readonly IReadOnlyDictionary<string, Func<string, IReadOnlyList<RouteInfo>>> Routes =
        new Dictionary<string, Func<string, IReadOnlyList<RouteInfo>>>
        {
            ["iproute2-route-json"] = ParseIpRoute,
        };

    private static readonly string[] ContainerBridgePrefixes = ["docker", "podman", "cni", "br-", "virbr"];

    /// <summary>`ip -j -d addr show`（-dでlinkinfo.info_kindが付く）。</summary>
    private static IReadOnlyList<NetworkInterfaceInfo> ParseIpAddr(string output) =>
        Array(output).Select(i =>
        {
            var name = i.GetProperty("ifname").GetString()!;
            var infoKind = i.TryGetProperty("linkinfo", out var linkinfo) && linkinfo.TryGetProperty("info_kind", out var k)
                ? k.GetString() : null;
            var kind = Kind(String(i, "link_type"), infoKind);
            var master = String(i, "master");
            var containerNetwork = kind == InterfaceKind.Veth
                || (kind == InterfaceKind.Bridge && ContainerBridgePrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)));

            return new NetworkInterfaceInfo(
                Name: name,
                Kind: kind,
                MacAddress: kind == InterfaceKind.Loopback ? null : String(i, "address"),
                State: String(i, "operstate") ?? "UNKNOWN",
                Mtu: i.TryGetProperty("mtu", out var mtu) ? mtu.GetInt32() : null,
                Master: master,
                ContainerNetwork: containerNetwork,
                Addresses: i.TryGetProperty("addr_info", out var addrs)
                    ? addrs.EnumerateArray()
                        .Where(a => a.TryGetProperty("local", out _))
                        .Select(a => new InterfaceAddress(
                            Family: a.GetProperty("family").GetString() == "inet6" ? "ipv6" : "ipv4",
                            Address: a.GetProperty("local").GetString()!,
                            PrefixLength: a.GetProperty("prefixlen").GetInt32(),
                            Dynamic: a.TryGetProperty("dynamic", out var dynamic) && dynamic.ValueKind == JsonValueKind.True))
                        .ToList()
                    : []);
        }).ToList();

    /// <summary>`ip -j route show`（IPv4とIPv6は別コマンドで取得し、プロバイダーが連結する）。</summary>
    private static IReadOnlyList<RouteInfo> ParseIpRoute(string output) =>
        Array(output)
            .Select(r =>
            {
                var destination = String(r, "dst") ?? "default";
                var gateway = String(r, "gateway");
                return new RouteInfo(
                    Family: (gateway ?? destination).Contains(':') ? "ipv6" : "ipv4",
                    Destination: destination,
                    Gateway: gateway,
                    Device: String(r, "dev"),
                    Protocol: String(r, "protocol"),
                    Metric: r.TryGetProperty("metric", out var metric) ? metric.GetInt32() : null);
            })
            .ToList();

    private static InterfaceKind Kind(string? linkType, string? infoKind) => (linkType, infoKind) switch
    {
        ("loopback", _) => InterfaceKind.Loopback,
        (_, "bridge") => InterfaceKind.Bridge,
        (_, "bond") => InterfaceKind.Bond,
        (_, "vlan") => InterfaceKind.Vlan,
        (_, "veth") => InterfaceKind.Veth,
        (_, "dummy") => InterfaceKind.Dummy,
        (_, "ipip" or "gre" or "vxlan" or "wireguard" or "tun") => InterfaceKind.Tunnel,
        ("ether", null) => InterfaceKind.Physical,
        _ => InterfaceKind.Other,
    };

    private static IEnumerable<JsonElement> Array(string json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonDocument.Parse(json).RootElement.EnumerateArray().Select(e => e.Clone()).ToList();

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>/etc/resolv.conf の nameserver と search。</summary>
    public static (IReadOnlyList<string> Servers, IReadOnlyList<string> Search) ParseResolvConf(string text)
    {
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#') && !l.StartsWith(';'))
            .Select(l => l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToList();
        return (
            lines.Where(t => t[0] == "nameserver" && t.Length > 1).Select(t => t[1]).ToList(),
            lines.Where(t => t[0] is "search" or "domain").SelectMany(t => t.Skip(1)).Distinct().ToList());
    }
}
