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
}
