using SystemAgent.Core.CapabilityProviders;
using SystemAgent.Infrastructure.CapabilityProviders.Network;
using SystemAgent.Infrastructure.Commands;

namespace SystemAgent.Core.Tests;

public class NetworkTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void IpAddr_ClassifiesInterfaces()
    {
        var interfaces = NetworkOutputParsers.Interfaces["iproute2-addr-json"](Fixture("ip-addr-alma.json"));
        var byName = interfaces.ToDictionary(i => i.Name);

        Assert.Equal(InterfaceKind.Loopback, byName["lo"].Kind);
        Assert.Null(byName["lo"].MacAddress);

        var eth0 = byName["eth0"];
        Assert.Equal(InterfaceKind.Physical, eth0.Kind);
        Assert.False(eth0.ContainerNetwork);
        Assert.Equal("UP", eth0.State);
        Assert.Equal(1500, eth0.Mtu);
        Assert.Contains(eth0.Addresses, a => a is { Family: "ipv4", Address: "172.30.170.123", PrefixLength: 20 });
        Assert.Contains(eth0.Addresses, a => a.Family == "ipv6");

        Assert.Equal(InterfaceKind.Bridge, byName["podman0"].Kind);
        Assert.True(byName["podman0"].ContainerNetwork);
        Assert.True(byName["docker0"].ContainerNetwork);

        Assert.Equal(InterfaceKind.Veth, byName["veth0"].Kind);
        Assert.Equal("podman0", byName["veth0"].Master);
        Assert.True(byName["veth0"].ContainerNetwork);

        Assert.Equal(InterfaceKind.Dummy, byName["sa-dummy0"].Kind);
        Assert.False(byName["sa-dummy0"].ContainerNetwork);
        Assert.Equal("192.0.2.10/24", byName["sa-dummy0"].Addresses[0].ToString());
    }

    [Fact]
    public void IpRoute_ParsesDefaultAndLinkRoutes()
    {
        var routes = NetworkOutputParsers.Routes["iproute2-route-json"](Fixture("ip-route-alma.json"));

        var def = Assert.Single(routes, r => r.Destination == "default");
        Assert.Equal("172.30.160.1", def.Gateway);
        Assert.Equal("eth0", def.Device);
        Assert.Equal("ipv4", def.Family);
        Assert.Contains(routes, r => r is { Destination: "172.30.160.0/20", Gateway: null, Protocol: "kernel" });

        var v6 = NetworkOutputParsers.Routes["iproute2-route-json"](Fixture("ip-route6-alma.json"));
        Assert.All(v6, r => Assert.Equal("ipv6", r.Family));
    }

    [Fact]
    public void ResolvConf_ParsesServersAndSearch()
    {
        var (servers, search) = NetworkOutputParsers.ParseResolvConf("""
            # comment
            nameserver 10.0.0.53
            nameserver fd00::53
            ; other comment
            search corp.local lab.local
            options edns0
            """);
        Assert.Equal(["10.0.0.53", "fd00::53"], servers);
        Assert.Equal(["corp.local", "lab.local"], search);
    }

    [Theory]
    [InlineData(null, "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin")]
    [InlineData("/usr/bin:/bin", "/usr/bin:/bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/sbin")]
    [InlineData("/opt/x:/usr/sbin", "/opt/x:/usr/sbin:/usr/local/sbin:/usr/local/bin:/usr/bin:/sbin:/bin")]
    public void WithSystemPaths_AppendsMissingSbinDirectories(string? path, string expected) =>
        Assert.Equal(expected, ProcessCommandRunner.WithSystemPaths(path));
}
