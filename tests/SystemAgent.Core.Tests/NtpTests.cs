using Microsoft.Extensions.Logging.Abstractions;
using SystemAgent.Core.CapabilityProviders;
using SystemAgent.Infrastructure.CapabilityProviders.Ntp;
using SystemAgent.Infrastructure.CapabilityProviders.Templates;
using SystemAgent.Infrastructure.Commands;
using FakeRunner = SystemAgent.Core.Tests.CommandTemplateTests.FakeRunner;

namespace SystemAgent.Core.Tests;

public sealed class NtpTests : IDisposable
{
    private static readonly string BuiltInDir = Path.Combine(AppContext.BaseDirectory, "CommandTemplates");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sa-ntp-tests-" + Guid.NewGuid());

    public NtpTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static CommandTemplateStore Store() =>
        new(BuiltInDir, null, NullLogger<CommandTemplateStore>.Instance);

    // --- テンプレート選択 ---

    [Theory]
    [InlineData("almalinux", new[] { "rhel", "centos", "fedora" }, "chronyc", "4.8", "chrony-rhel")]
    [InlineData("ubuntu", new[] { "debian" }, "chronyc", "4.5", "chrony-debian")]
    [InlineData("debian", new string[0], "timesyncd", "257.0", "timesyncd")]
    public void Resolve_PicksTemplatePerOs(string osId, string[] idLike, string tool, string version, string expected)
    {
        var env = new HostEnvironment("h", osId, idLike, "1", osId, "x86_64", [new ToolInfo(tool, version)]);
        Assert.Equal(expected, Store().Resolve("ntp", tool, env)?.Id);
    }

    [Fact]
    public void Resolve_OldSystemdWithoutShowTimesync_HasNoTemplate()
    {
        var env = new HostEnvironment("h", "ubuntu", ["debian"], "18.04", "u", "x86_64", [new ToolInfo("timesyncd", "237.0")]);
        Assert.Null(Store().Resolve("ntp", "timesyncd", env));
    }

    // --- パーサー（WSL上の実出力） ---

    [Fact]
    public void ChronyTracking_Synchronized()
    {
        var part = NtpOutputParsers.Status["chrony-tracking-csv"](Fixture("chrony-ubuntu-tracking.csv"));
        Assert.True(part.Synchronized);
        Assert.Equal("91.189.91.157", part.CurrentSource);
        Assert.Equal(3, part.Stratum);
        Assert.True(part.OffsetSeconds < 0); // CSVの正値は「遅れ」
    }

    [Fact]
    public void ChronyTracking_NotSynchronised()
    {
        // AlmaLinux 9 (WSL) で起動直後に実際に出力された行
        const string notSynced = "00000000,,0,0.000000000,3.603757620,0.113657385,0.155346483,-100049.070,0.000,0.000,1.000000000,1.000000000,4.4,Not synchronised";
        var part = NtpOutputParsers.Status["chrony-tracking-csv"](notSynced);
        Assert.False(part.Synchronized);
        Assert.Null(part.CurrentSource);
        Assert.Null(part.Stratum);
    }

    [Fact]
    public void ChronySources_MapsStates()
    {
        var ubuntu = NtpOutputParsers.Sources["chrony-sources-csv"](Fixture("chrony-ubuntu-sources.csv"));
        Assert.Equal(8, ubuntu.Count);
        Assert.Equal([NtpSourceState.NotCombined, NtpSourceState.Falseticker, NtpSourceState.TooVariable],
            ubuntu.Select(s => s.State).Distinct().Order());
        Assert.Equal(3, ubuntu.Single(s => s.Address == "162.159.200.123").Stratum);

        var sources = NtpOutputParsers.Sources["chrony-sources-csv"](Fixture("chrony-alma-sources.csv"));
        Assert.Equal("140.245.90.15", Assert.Single(sources, s => s.State == NtpSourceState.Selected).Address);
        Assert.All(sources, s => Assert.True(s.Reachable));
    }

    [Fact]
    public void TimesyncdShow_UsesFallbackWhenNothingConfigured()
    {
        var part = NtpOutputParsers.Status["timesyncd-show"](Fixture("timesyncd-show.txt"));
        Assert.Null(part.Synchronized);
        Assert.Equal("0.debian.pool.ntp.org (208.88.66.8)", part.CurrentSource);
        Assert.Equal(2, part.Stratum);
        Assert.Equal(4, part.Servers!.Count);
    }

    // --- 設定の書き換え（WSL上の実設定ファイル） ---

    [Theory]
    [InlineData("chrony-alma.conf")]
    [InlineData("chrony-ubuntu.conf")]
    public void ChronyWriter_ReplacesSourcesAndIsIdempotent(string fixture)
    {
        var writer = NtpConfigWriters.ByStyle["chrony-managed-block"];
        var original = Fixture(fixture);
        Assert.NotEmpty(writer.ReadServers(original));

        var once = writer.Apply(original, ["10.0.0.1", "ntp.local"]);
        var twice = writer.Apply(once, ["10.0.0.1", "ntp.local"]);

        Assert.Equal(["10.0.0.1", "ntp.local"], writer.ReadServers(once));
        Assert.Equal(once, twice);
        Assert.Contains(ChronyManagedBlockWriter.DisabledPrefix + "pool", once);
        // server/pool以外の行（sourcedir等）はそのまま残す
        Assert.Contains("\nsourcedir /run/chrony-dhcp", once);

        var changed = writer.Apply(once, ["10.0.0.2"]);
        Assert.Equal(["10.0.0.2"], writer.ReadServers(changed));
    }

    [Fact]
    public void TimesyncdWriter_WritesDropIn()
    {
        var writer = NtpConfigWriters.ByStyle["timesyncd-dropin"];
        var text = writer.Apply("", ["10.0.0.1", "10.0.0.2"]);
        Assert.Contains("[Time]\nNTP=10.0.0.1 10.0.0.2\n", text);
        Assert.Equal(["10.0.0.1", "10.0.0.2"], writer.ReadServers(text));
    }

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("fd00::1")]
    [InlineData("ntp1.corp.local")]
    public void Validate_AcceptsHostsAndAddresses(string server) => Assert.Single(TemplateNtpProvider.Validate([server]));

    [Theory]
    [InlineData("-x")]
    [InlineData("a b")]
    [InlineData("host;rm")]
    [InlineData("")]
    public void Validate_Rejects(string server) =>
        Assert.Throws<ArgumentException>(() => TemplateNtpProvider.Validate([server]));

    // --- 適用とロールバック ---

    private TemplateNtpProvider ProviderWithConfigAt(string configFile, FakeRunner runner)
    {
        var template = Store().Templates.Single(t => t.Id == "chrony-rhel");
        template = template with { Settings = new Dictionary<string, string>(template.Settings) { ["configFile"] = configFile } };
        return new TemplateNtpProvider(new TemplateCommandExecutor(template, runner), NullLogger.Instance);
    }

    [Fact]
    public async Task SetServers_BacksUpOriginalOnce_AndRestartsService()
    {
        var config = Path.Combine(_dir, "chrony.conf");
        File.WriteAllText(config, Fixture("chrony-alma.conf"));
        var runner = new FakeRunner();
        var provider = ProviderWithConfigAt(config, runner);

        await provider.SetServersAsync(["10.0.0.1"]);
        await provider.SetServersAsync(["10.0.0.2"]);

        Assert.Equal(Fixture("chrony-alma.conf"), File.ReadAllText(config + TemplateNtpProvider.BackupSuffix));
        Assert.Contains("server 10.0.0.2 iburst", File.ReadAllText(config));
        Assert.Equal(["systemctl restart chronyd", "systemctl restart chronyd"], runner.Calls);
    }

    [Fact]
    public async Task SetServers_RestoresConfigWhenRestartFails()
    {
        var config = Path.Combine(_dir, "chrony.conf");
        var original = Fixture("chrony-alma.conf");
        File.WriteAllText(config, original);
        var restarts = 0;
        var runner = new FakeRunner
        {
            // 1回目（新設定での再起動）だけ失敗させる
            Respond = call => call.StartsWith("systemctl restart") && restarts++ == 0
                ? new CommandResult(1, "", "Job for chronyd.service failed") : null,
        };
        var provider = ProviderWithConfigAt(config, runner);

        await Assert.ThrowsAsync<CommandFailedException>(() => provider.SetServersAsync(["10.0.0.1"]));

        Assert.Equal(original, File.ReadAllText(config));
        Assert.Equal(2, runner.Calls.Count(c => c.StartsWith("systemctl restart")));
    }
}
