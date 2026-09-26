using SystemAgent.Core.Ha;
using SystemAgent.Infrastructure.Ha;

namespace SystemAgent.Core.Tests;

public sealed class KeepalivedTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sa-ha-tests-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static HaSettings Settings(string? authPass = "secret1", IReadOnlyList<string>? peers = null) => new(
        true, "eth0", "10.0.0.100/24", 51, 100, authPass, peers ?? [], peers is { Count: > 0 } ? "10.0.0.11" : null,
        HaReturnMode.Manual, null, "repl", "replpass");

    [Fact]
    public void Generate_BackupNopreemptWithNotifyHooks()
    {
        var conf = KeepalivedConfig.Generate(Settings(), "node-a");

        Assert.Contains("state BACKUP", conf);
        Assert.Contains("nopreempt", conf);
        Assert.Contains("virtual_router_id 51", conf);
        Assert.Contains("priority 100", conf);
        Assert.Contains("auth_pass secret1", conf);
        Assert.Contains("10.0.0.100/24 dev eth0", conf);
        Assert.Contains("notify_master \"/usr/bin/systemagent ha notify MASTER\"", conf);
        Assert.Contains("notify_backup \"/usr/bin/systemagent ha notify BACKUP\"", conf);
        Assert.Contains("enable_script_security", conf);
        Assert.DoesNotContain("unicast_peer", conf);
        // DB接続情報は設定ファイルに書かない
        Assert.DoesNotContain("replpass", conf);
    }

    [Fact]
    public void Generate_UnicastAndNoAuth()
    {
        var conf = KeepalivedConfig.Generate(Settings(authPass: "", peers: ["10.0.0.12", "10.0.0.13"]), "node-a");

        Assert.DoesNotContain("authentication", conf);
        Assert.Contains("unicast_src_ip 10.0.0.11", conf);
        Assert.Contains("        10.0.0.12\n", conf);
    }

    [Theory]
    [InlineData("eth0\n}\nvrrp_script x {", "10.0.0.100/24", 51, 100, null)]
    [InlineData("eth0", "10.0.0.100", 51, 100, null)]
    [InlineData("eth0", "10.0.0.300/24", 51, 100, null)]
    [InlineData("eth0", "10.0.0.100/24", 0, 100, null)]
    [InlineData("eth0", "10.0.0.100/24", 51, 255, null)]
    [InlineData("eth0", "10.0.0.100/24", 51, 100, "toolongpass")]
    [InlineData("eth0", "10.0.0.100/24", 51, 100, "pa\"ss")]
    public void Validate_RejectsUnsafeOrInvalidValues(string iface, string vip, int vrid, int priority, string? pass) =>
        Assert.Throws<ArgumentException>(() => KeepalivedConfig.Validate(Settings(pass) with
        {
            Interface = iface, VirtualIp = vip, VirtualRouterId = vrid, Priority = priority,
        }));

    [Fact]
    public void Validate_RejectsBadUnicastPeer() =>
        Assert.Throws<ArgumentException>(() => KeepalivedConfig.Validate(Settings(peers: ["10.0.0.12\n}"])));

    [Fact]
    public void SourceHost_DefaultsToVip() =>
        Assert.Equal("10.0.0.100", Settings().SourceHost);

    [Fact]
    public void StateStore_HookTokenIsStableAndVerified()
    {
        var store = new HaStateStore(_dir);
        var token = store.EnsureHookToken();

        Assert.Equal(token, store.EnsureHookToken());
        Assert.True(store.VerifyHookToken(token));
        Assert.False(store.VerifyHookToken("wrong"));
        Assert.False(store.VerifyHookToken(null));

        store.Update(s => s with { State = VrrpState.Master, History = [.. s.History, new HaEvent(DateTimeOffset.UtcNow, "x")] });
        Assert.Equal(VrrpState.Master, new HaStateStore(_dir).Load().State);
    }
}
