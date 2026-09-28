using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SystemAgent.Core.CapabilityProviders;
using SystemAgent.Infrastructure.CapabilityProviders.Services;
using SystemAgent.Infrastructure.CapabilityProviders.Templates;
using FakeRunner = SystemAgent.Core.Tests.CommandTemplateTests.FakeRunner;

namespace SystemAgent.Core.Tests;

public class ServiceTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void SystemctlShow_Running()
    {
        var s = ServiceOutputParsers.Status["systemctl-show"](Fixture("systemctl-show-chronyd.txt"));
        Assert.Equal("chronyd", s.Name);
        Assert.True(s.Exists);
        Assert.Equal(("active", "running", "enabled"), (s.ActiveState, s.SubState, s.UnitFileState));
        Assert.Equal(140, s.MainPid);
        Assert.Equal(new DateTime(2026, 9, 26, 14, 35, 51), s.ActiveSince!.Value.LocalDateTime);
    }

    [Fact]
    public void SystemctlShow_NotFoundAndMasked()
    {
        var missing = ServiceOutputParsers.Status["systemctl-show"](Fixture("systemctl-show-notfound.txt"));
        Assert.False(missing.Exists);
        Assert.Null(missing.MainPid);
        Assert.Null(missing.ActiveSince);

        var masked = ServiceOutputParsers.Status["systemctl-show"](Fixture("systemctl-show-inactive.txt"));
        Assert.True(masked.Exists);
        Assert.Equal("masked", masked.UnitFileState);
    }

    [Theory]
    [InlineData("chronyd")]
    [InlineData("podman.socket")]
    [InlineData("getty@tty1.service")]
    public void ValidUnitNames(string unit) => Assert.Equal(unit, TemplateServiceManagerProvider.ValidateUnit(unit));

    [Theory]
    [InlineData("--all")]
    [InlineData("a b")]
    [InlineData("x;reboot")]
    [InlineData("")]
    public void InvalidUnitNames(string unit) => Assert.Throws<ArgumentException>(() => TemplateServiceManagerProvider.ValidateUnit(unit));

    private static ServiceManagement Management(params string[] managed)
    {
        var values = managed.Select((m, i) => new KeyValuePair<string, string?>($"Services:Managed:{i}", m));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var store = new CommandTemplateStore(Path.Combine(AppContext.BaseDirectory, "CommandTemplates"), null, NullLogger<CommandTemplateStore>.Instance);
        return new ServiceManagement(new CapabilityTemplateResolver(null!, store, new FakeRunner(), configuration), configuration);
    }

    [Fact]
    public void Deny_UnmanagedAndProtectedServices()
    {
        var management = Management("chronyd", "sshd", "systemagent");

        Assert.Null(management.Deny("chronyd", ServiceAction.Restart));
        Assert.Null(management.Deny("chronyd.service", ServiceAction.Stop));
        Assert.NotNull(management.Deny("httpd", ServiceAction.Start));           // 管理対象外
        Assert.Null(management.Deny("sshd", ServiceAction.Restart));             // 再起動は可
        Assert.NotNull(management.Deny("sshd", ServiceAction.Stop));             // 停止は不可
        Assert.NotNull(management.Deny("systemagent", ServiceAction.Disable));   // 自身の無効化は不可
    }

    [Fact]
    public void DefaultManagedList_IsUsedWhenNotConfigured() =>
        Assert.Contains("chronyd", Management().Managed);

    [Fact]
    public async Task Provider_RendersSystemctlAndJournalctl()
    {
        var runner = new FakeRunner { Result = new(0, Fixture("systemctl-show-chronyd.txt"), "") };
        var store = new CommandTemplateStore(Path.Combine(AppContext.BaseDirectory, "CommandTemplates"), null, NullLogger<CommandTemplateStore>.Instance);
        var provider = new TemplateServiceManagerProvider(new TemplateCommandExecutor(store.Templates.Single(t => t.Id == "systemctl"), runner));

        await provider.ExecuteAsync("chronyd", ServiceAction.Restart);
        await provider.GetLogsAsync("chronyd", 50);

        Assert.Equal("systemctl restart chronyd", runner.Calls[0]);
        Assert.Equal("journalctl -u chronyd -n 50 --no-pager -o short-iso", runner.Calls[1]);
    }

    [Fact]
    public async Task ListExisting_QueriesInParallel_KeepsOrderAndRemovesAliases()
    {
        var management = Management("chronyd", "mysqld", "mariadb", "nginx", "sshd", "docker", "podman", "keepalived");
        var provider = new SlowProvider(TimeSpan.FromMilliseconds(300));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var statuses = await management.ListExistingAsync(provider, CancellationToken.None);

        // 8件 × 300ms を直列なら2.4秒。4件ずつ並行なら約0.6秒
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1.8), $"{watch.Elapsed}");
        Assert.InRange(provider.MaxConcurrency, 2, ServiceManagement.MaxParallelStatus);
        // mysqld は mariadb の別名、docker は存在しない
        Assert.Equal(["chronyd", "mariadb", "nginx", "sshd", "podman", "keepalived"], statuses.Select(s => s.Name));
    }

    private sealed class SlowProvider(TimeSpan delay) : IServiceManagerProvider
    {
        private int _running;
        public int MaxConcurrency { get; private set; }

        public async Task<ServiceStatus> GetStatusAsync(string unit, CancellationToken cancellationToken = default)
        {
            var running = Interlocked.Increment(ref _running);
            lock (this) MaxConcurrency = Math.Max(MaxConcurrency, running);
            await Task.Delay(delay, cancellationToken);
            Interlocked.Decrement(ref _running);
            var name = unit == "mysqld" ? "mariadb" : unit;
            return new ServiceStatus(name, "", unit != "docker", "loaded", "active", "running", "enabled", 1, null);
        }

        public Task ExecuteAsync(string unit, ServiceAction action, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<string> GetLogsAsync(string unit, int lines, CancellationToken cancellationToken = default) => Task.FromResult("");
    }
}
