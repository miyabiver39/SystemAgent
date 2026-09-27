using Microsoft.Extensions.Logging.Abstractions;
using SystemAgent.Core.CapabilityProviders;
using SystemAgent.Core.Errors;
using SystemAgent.Infrastructure.CapabilityProviders.Containers;
using SystemAgent.Infrastructure.CapabilityProviders.Templates;
using SystemAgent.Infrastructure.Commands;
using SystemAgent.Infrastructure.Security;
using FakeRunner = SystemAgent.Core.Tests.CommandTemplateTests.FakeRunner;

namespace SystemAgent.Core.Tests;

public sealed class ImageTransferTests : IDisposable
{
    private const string Target = "zot.local:5000/app/web:1.0";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sa-transfer-tests-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static TemplateContainerRuntimeProvider Runtime(FakeRunner runner)
    {
        var store = new CommandTemplateStore(Path.Combine(AppContext.BaseDirectory, "CommandTemplates"), null, NullLogger<CommandTemplateStore>.Instance);
        return new TemplateContainerRuntimeProvider(
            new TemplateCommandExecutor(store.Templates.Single(t => t.Id == "podman"), runner), new RuntimeInfo("podman", "5", "podman"));
    }

    private async Task<ImageTransferService> ServiceAsync()
    {
        var registries = new RegistryService(
            new LocalSecretStore(Path.Combine(_dir, "secrets"), NullLogger<LocalSecretStore>.Instance), NullLogger<RegistryService>.Instance);
        await registries.SaveAsync(Runtime(new FakeRunner()), "zot.local:5000", "deploy", "pass", tlsVerify: false);
        return new ImageTransferService(registries);
    }

    /// <param name="targetExists">送り先の名前がローカルに既にあるか。</param>
    /// <param name="pushFails">push を失敗させる。</param>
    private static FakeRunner Runner(string imagesJson, bool targetExists = false, bool pushFails = false) => new()
    {
        Respond = call => call switch
        {
            _ when call.StartsWith("podman images") => new CommandResult(0, imagesJson, ""),
            _ when call.StartsWith("podman image exists") => new CommandResult(targetExists ? 0 : 1, "", ""),
            _ when call.StartsWith("podman push") && pushFails => new CommandResult(125, "", "unauthorized"),
            _ => null,
        },
    };

    private static List<string> Commands(FakeRunner runner) =>
        runner.Calls.Where(c => !c.StartsWith("podman images") && !c.StartsWith("podman image exists")).ToList();

    [Fact]
    public async Task Push_TagsLogsInPushesAndUntags()
    {
        var runner = Runner("""[{"Id":"abc","Names":["localhost/web:1"]}]""");
        await (await ServiceAsync()).PushAsync(Runtime(runner), " localhost/web:1 ", Target);

        Assert.Equal(
        [
            "podman login --tls-verify=false --username deploy --password-stdin zot.local:5000",
            $"podman tag localhost/web:1 {Target}",
            $"podman push --tls-verify=false {Target}",
            $"podman rmi {Target}",
        ], Commands(runner));
    }

    [Fact]
    public async Task Push_KeepsTargetName_WhenItAlreadyExisted()
    {
        var runner = Runner("""[{"Id":"abc","Names":["localhost/web:1"]}]""", targetExists: true);
        await (await ServiceAsync()).PushAsync(Runtime(runner), "localhost/web:1", Target);
        Assert.DoesNotContain(Commands(runner), c => c.StartsWith("podman rmi"));
    }

    [Fact]
    public async Task Push_KeepsTargetName_ForUntaggedImage()
    {
        // 名前の無いイメージに付けた名前を外すとイメージ自体が消える
        var runner = Runner("""[{"Id":"sha256:abcdef123456","Names":[]}]""");
        await (await ServiceAsync()).PushAsync(Runtime(runner), "abcdef12", Target);
        Assert.DoesNotContain(Commands(runner), c => c.StartsWith("podman rmi"));
    }

    [Fact]
    public async Task Push_Untags_EvenWhenPushFails()
    {
        var service = await ServiceAsync();
        var runner = Runner("""[{"Id":"abc","Names":["localhost/web:1"]}]""", pushFails: true);
        await Assert.ThrowsAsync<CommandFailedException>(() => service.PushAsync(Runtime(runner), "localhost/web:1", Target));
        Assert.Equal($"podman rmi {Target}", Commands(runner)[^1]);
    }

    [Theory]
    [InlineData("app/web:1.0")]
    [InlineData("other.local:5000/app/web:1.0")]
    public async Task Push_RejectsTargetWithoutRegisteredRegistry(string target)
    {
        var service = await ServiceAsync();
        var runner = Runner("[]");
        await Assert.ThrowsAsync<ArgumentException>(() => service.PushAsync(Runtime(runner), "localhost/web:1", target));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Pull_LogsInOnlyForRegisteredRegistry()
    {
        var service = await ServiceAsync();
        var runner = new FakeRunner();
        await service.PullAsync(Runtime(runner), "docker.io/library/busybox:latest");
        await service.PullAsync(Runtime(runner), Target);
        Assert.Equal(
        [
            "podman pull --tls-verify=true docker.io/library/busybox:latest",
            "podman login --tls-verify=false --username deploy --password-stdin zot.local:5000",
            $"podman pull --tls-verify=false {Target}",
        ], runner.Calls);
    }
}
