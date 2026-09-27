using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SystemAgent.Core.CapabilityProviders;
using SystemAgent.Infrastructure.CapabilityProviders.Containers;
using SystemAgent.Infrastructure.CapabilityProviders.Templates;
using SystemAgent.Infrastructure.Commands;
using SystemAgent.Infrastructure.Security;
using FakeRunner = SystemAgent.Core.Tests.CommandTemplateTests.FakeRunner;
using SystemAgent.Core.Errors;

namespace SystemAgent.Core.Tests;

public sealed class RegistryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sa-registry-tests-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static TemplateContainerRuntimeProvider Runtime(FakeRunner runner, string templateId = "podman")
    {
        var store = new CommandTemplateStore(Path.Combine(AppContext.BaseDirectory, "CommandTemplates"), null, NullLogger<CommandTemplateStore>.Instance);
        return new TemplateContainerRuntimeProvider(
            new TemplateCommandExecutor(store.Templates.Single(t => t.Id == templateId), runner), new RuntimeInfo(templateId, "1", templateId));
    }

    private RegistryService Service() =>
        new(new LocalSecretStore(Path.Combine(_dir, "secrets"), NullLogger<LocalSecretStore>.Instance), NullLogger<RegistryService>.Instance);

    [Theory]
    [InlineData("nginx", "docker.io")]
    [InlineData("library/nginx:1.27", "docker.io")]
    [InlineData("registry.example.com/app/web:1.0", "registry.example.com")]
    [InlineData("Registry.Example.com:5000/web", "registry.example.com:5000")]
    [InlineData("localhost/web", "localhost")]
    [InlineData("index.docker.io/library/nginx", "docker.io")]
    public void FromImage_ResolvesRegistry(string image, string expected) =>
        Assert.Equal(expected, RegistryName.FromImage(image));

    [Theory]
    [InlineData("-registry")]
    [InlineData("registry.example.com/path")]
    [InlineData("user@registry")]
    [InlineData("")]
    [InlineData("registry:port")]
    public void Validate_RejectsInvalid(string registry) =>
        Assert.Throws<ArgumentException>(() => RegistryName.Validate(registry));

    [Theory]
    [InlineData("podman", "podman login --tls-verify=true --username deploy --password-stdin registry.example.com")]
    [InlineData("docker", "docker login --username deploy --password-stdin registry.example.com")]
    public async Task Login_PassesPasswordViaStdinNotArguments(string templateId, string expected)
    {
        var runner = new FakeRunner();
        await Runtime(runner, templateId).LoginAsync("registry.example.com", "deploy", "s3cr3t-token");

        var call = Assert.Single(runner.Calls);
        Assert.Equal(expected, call);
        Assert.DoesNotContain("s3cr3t-token", call);
        Assert.Equal("s3cr3t-token", Encoding.UTF8.GetString(runner.ReceivedInput));
    }

    [Fact]
    public async Task Login_RejectsOptionLikeUsername() =>
        await Assert.ThrowsAsync<ArgumentException>(() => Runtime(new FakeRunner()).LoginAsync("registry.example.com", "--tls-verify=false", "x"));

    [Fact]
    public async Task Save_StoresOnlyAfterSuccessfulLogin()
    {
        var service = Service();
        var failing = new FakeRunner { Result = new CommandResult(125, "", "unauthorized: authentication required") };

        await Assert.ThrowsAsync<CommandFailedException>(() => service.SaveAsync(Runtime(failing), "registry.example.com", "deploy", "wrong"));
        Assert.Empty(service.List());

        await service.SaveAsync(Runtime(new FakeRunner()), "Registry.Example.com", "deploy", "right");
        var saved = Assert.Single(service.List());
        Assert.Equal(("registry.example.com", "deploy"), (saved.Registry, saved.Username));
    }

    [Fact]
    public async Task Save_WithoutPassword_ReusesStoredPassword()
    {
        var service = Service();
        await service.SaveAsync(Runtime(new FakeRunner()), "registry.example.com", "deploy", "stored-pass");

        var runner = new FakeRunner();
        await service.SaveAsync(Runtime(runner), "registry.example.com", "deploy", null);
        Assert.Equal("stored-pass", Encoding.UTF8.GetString(runner.ReceivedInput));

        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(Runtime(new FakeRunner()), "other.example.com", "deploy", null));
    }

    [Fact]
    public async Task EnsureLogin_LogsInOnlyForRegisteredRegistry()
    {
        var service = Service();
        await service.SaveAsync(Runtime(new FakeRunner()), "registry.example.com", "deploy", "pass");

        var runner = new FakeRunner();
        await service.EnsureLoginAsync(Runtime(runner), "docker.io/library/nginx:latest");
        Assert.Empty(runner.Calls);

        Assert.True(await service.EnsureLoginAsync(Runtime(runner), "registry.example.com/app/web:1.0"));
        Assert.Equal("podman login --tls-verify=true --username deploy --password-stdin registry.example.com", Assert.Single(runner.Calls));
    }

    [Fact]
    public async Task TlsVerifyDisabled_IsStoredAndPassedToPodman()
    {
        var service = Service();
        var runner = new FakeRunner();
        await service.SaveAsync(Runtime(runner), "zot.local:5000", "deploy", "pass", tlsVerify: false);
        Assert.Equal("podman login --tls-verify=false --username deploy --password-stdin zot.local:5000", Assert.Single(runner.Calls));
        Assert.False(Assert.Single(service.List()).TlsVerify);

        var pull = new FakeRunner();
        var provider = Runtime(pull);
        var tlsVerify = await service.EnsureLoginAsync(provider, "zot.local:5000/app/web:1.0");
        await provider.PullImageAsync("zot.local:5000/app/web:1.0", tlsVerify);
        Assert.Equal("podman pull --tls-verify=false zot.local:5000/app/web:1.0", pull.Calls[^1]);
    }

    [Theory]
    [InlineData("podman", "podman tag localhost/web:1 zot.local:5000/app/web:1.0", "podman push --tls-verify=false zot.local:5000/app/web:1.0")]
    [InlineData("docker", "docker tag localhost/web:1 zot.local:5000/app/web:1.0", "docker push zot.local:5000/app/web:1.0")]
    public async Task TagAndPush_RenderTemplateCommands(string templateId, string tag, string push)
    {
        var runner = new FakeRunner();
        var provider = Runtime(runner, templateId);
        await provider.TagImageAsync("localhost/web:1", "zot.local:5000/app/web:1.0");
        await provider.PushImageAsync("zot.local:5000/app/web:1.0", tlsVerify: false);
        Assert.Equal([tag, push], runner.Calls);
    }

    [Fact]
    public async Task Remove_DeletesEvenIfLogoutFails()
    {
        var service = Service();
        await service.SaveAsync(Runtime(new FakeRunner()), "registry.example.com", "deploy", "pass");

        var runner = new FakeRunner { Result = new CommandResult(125, "", "not logged into registry.example.com") };
        Assert.True(await service.RemoveAsync(Runtime(runner), "registry.example.com"));
        Assert.Empty(service.List());
        Assert.False(await service.RemoveAsync(Runtime(runner), "registry.example.com"));
    }
}
