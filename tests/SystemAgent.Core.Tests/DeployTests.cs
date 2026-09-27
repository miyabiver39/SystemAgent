using Microsoft.Extensions.Logging.Abstractions;
using SystemAgent.Core.CapabilityProviders;
using SystemAgent.Core.Deploy;
using SystemAgent.Infrastructure.CapabilityProviders.Containers;
using SystemAgent.Infrastructure.CapabilityProviders.Templates;
using SystemAgent.Infrastructure.Cluster;
using SystemAgent.Infrastructure.Deploy;
using SystemAgent.Infrastructure.Security;
using FakeRunner = SystemAgent.Core.Tests.CommandTemplateTests.FakeRunner;
using SystemAgent.Core.Errors;

namespace SystemAgent.Core.Tests;

public sealed class DeployTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sa-deploy-tests-" + Guid.NewGuid());
    private readonly FakeRuntime _runtime = new();

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private DeploymentService Service()
    {
        var secrets = new LocalSecretStore(Path.Combine(_dir, "secrets"), NullLogger<LocalSecretStore>.Instance);
        return new DeploymentService(secrets, _runtime, new ImageTransferService(new RegistryService(secrets, NullLogger<RegistryService>.Instance)),
            NullLogger<DeploymentService>.Instance);
    }

    private static DeploymentSpec Spec(string name = "web", params string[] env) =>
        new(name, "registry.example.com/app/web", ["8080:80"], env, ["/srv/web:/data:ro"], HealthCheckSeconds: 0);

    [Fact]
    public async Task Deploy_ReplacesContainerAndTracksPreviousTag()
    {
        var service = Service();
        service.Save(Spec());

        await service.DeployAsync("web", "1.0", "admin");
        var view = await service.DeployAsync("web", "1.1", "admin");

        Assert.Equal(("1.1", "1.0"), (view.CurrentTag, view.PreviousTag));
        var container = Assert.Single(_runtime.Containers);
        Assert.Equal(("web", "registry.example.com/app/web:1.1", ContainerState.Running), (container.Name, container.Image, container.State));
        Assert.Equal(["registry.example.com/app/web:1.0", "registry.example.com/app/web:1.1"], _runtime.Pulled);
        Assert.Equal(2, view.History.Count);
        Assert.True(view.History.All(h => h.Success));
    }

    [Fact]
    public async Task Deploy_WhenNewContainerStops_RestoresPreviousContainer()
    {
        var service = Service();
        service.Save(Spec());
        await service.DeployAsync("web", "1.0", "admin");
        var originalId = _runtime.Containers.Single().Id;

        _runtime.CrashingTags.Add("2.0");
        var ex = await Assert.ThrowsAsync<DeploymentFailedException>(() => service.DeployAsync("web", "2.0", "admin"));
        Assert.Contains("正常に動作し続けませんでした", ex.Message);
        Assert.Contains("crash log", ex.Message);

        var container = Assert.Single(_runtime.Containers);
        Assert.Equal((originalId, "web", ContainerState.Running), (container.Id, container.Name, container.State));
        var view = (await service.GetAsync("web"))!;
        Assert.Equal("1.0", view.CurrentTag);
        Assert.False(view.History[0].Success);
    }

    [Fact]
    public async Task Deploy_WhenNewContainerIsInRestartLoop_RestoresPreviousContainer()
    {
        var service = Service();
        service.Save(Spec());
        await service.DeployAsync("web", "1.0", "admin");

        _runtime.RestartLoopTags.Add("2.0");
        var ex = await Assert.ThrowsAsync<DeploymentFailedException>(() => service.DeployAsync("web", "2.0", "admin"));
        Assert.Contains("3 回再起動しました", ex.Message);
        Assert.Equal("registry.example.com/app/web:1.0", Assert.Single(_runtime.Containers).Image);
    }

    [Fact]
    public async Task Deploy_WhenRunFails_RestoresPreviousContainer()
    {
        var service = Service();
        service.Save(Spec());
        await service.DeployAsync("web", "1.0", "admin");

        _runtime.FailingRunTags.Add("2.0");
        await Assert.ThrowsAsync<CommandFailedException>(() => service.DeployAsync("web", "2.0", "admin"));

        var container = Assert.Single(_runtime.Containers);
        Assert.Equal(("web", "registry.example.com/app/web:1.0", ContainerState.Running), (container.Name, container.Image, container.State));
    }

    [Fact]
    public async Task Deploy_WhenPullFails_LeavesCurrentContainerUntouched()
    {
        var service = Service();
        service.Save(Spec());
        await service.DeployAsync("web", "1.0", "admin");

        _runtime.MissingTags.Add("9.9");
        var stopsBefore = _runtime.StopCount;
        await Assert.ThrowsAsync<CommandFailedException>(() => service.DeployAsync("web", "9.9", "admin"));
        Assert.Equal(stopsBefore, _runtime.StopCount);
        Assert.Equal(ContainerState.Running, Assert.Single(_runtime.Containers).State);
    }

    [Fact]
    public async Task Deploy_WithPullMissing_UsesLocalImage()
    {
        var service = Service();
        service.Save(Spec());
        _runtime.LocalImages.Add("registry.example.com/app/web:1.0");

        await service.DeployAsync("web", "1.0", "admin");
        Assert.Empty(_runtime.Pulled);
    }

    [Fact]
    public async Task Rollback_DeploysPreviousTag()
    {
        var service = Service();
        service.Save(Spec());
        await Assert.ThrowsAsync<NotFoundException>(() => service.RollbackAsync("nothing", "admin"));
        await service.DeployAsync("web", "1.0", "admin");
        await Assert.ThrowsAsync<ClusterStateException>(() => service.RollbackAsync("web", "admin"));
        await service.DeployAsync("web", "1.1", "admin");

        var view = await service.RollbackAsync("web", "admin");
        Assert.Equal(("1.0", "1.1"), (view.CurrentTag, view.PreviousTag));
        Assert.Equal("rollback", view.History[0].Action);
    }

    [Fact]
    public async Task SecretEnvironmentValues_AreMaskedAndPreservedOnSave()
    {
        var service = Service();
        service.Save(Spec("web", "TZ=Asia/Tokyo", "DB_PASSWORD=p@ss=word"));
        var view = (await service.GetAsync("web"))!;
        Assert.Equal(["TZ=Asia/Tokyo", $"DB_PASSWORD={DeploymentSpecs.Masked}"], view.Spec.Environment);

        // 画面から伏せ字のまま保存し直しても値は失われない
        service.Save(view.Spec with { Environment = [.. view.Spec.Environment, "MODE=prod"] });
        await service.DeployAsync("web", "1.0", "admin");
        Assert.Contains("DB_PASSWORD=p@ss=word", _runtime.LastRun!.Environment);
        Assert.Contains("MODE=prod", _runtime.LastRun.Environment);

        Assert.Throws<ArgumentException>(() => service.Save(Spec("api", $"API_TOKEN={DeploymentSpecs.Masked}")));
    }

    [Theory]
    [InlineData("web-previous", "registry.example.com/app/web", "8080:80", "A=1", "/a:/b")]
    [InlineData("-web", "registry.example.com/app/web", "8080:80", "A=1", "/a:/b")]
    [InlineData("web", "registry.example.com/app/web:1.0", "8080:80", "A=1", "/a:/b")]
    [InlineData("web", "registry.example.com/app/web@sha256:abc", "8080:80", "A=1", "/a:/b")]
    [InlineData("web", "registry.example.com/app/web", "--privileged", "A=1", "/a:/b")]
    [InlineData("web", "registry.example.com/app/web", "8080:80", "1A=1", "/a:/b")]
    [InlineData("web", "registry.example.com/app/web", "8080:80", "A=line\nbreak", "/a:/b")]
    [InlineData("web", "registry.example.com/app/web", "8080:80", "A=1", "../etc:/b")]
    public void Validate_RejectsInvalidSpec(string name, string image, string port, string env, string volume) =>
        Assert.Throws<ArgumentException>(() => DeploymentSpecs.Validate(new DeploymentSpec(name, image, [port], [env], [volume])));

    [Fact]
    public void Validate_RejectsPortsWithPodAndDuplicateEnv()
    {
        Assert.Throws<ArgumentException>(() => DeploymentSpecs.Validate(Spec() with { Pod = "app" }));
        Assert.Throws<ArgumentException>(() => DeploymentSpecs.Validate(Spec("web", "A=1", "A=2")));
        var ok = DeploymentSpecs.Validate(new DeploymentSpec(" web ", "localhost:5050/app/web", [" ", "127.0.0.1:8080:80/tcp"], [], ["data:/var/lib/app"]));
        Assert.Equal(("web", 1), (ok.Name, ok.Ports.Count));
    }

    [Theory]
    [InlineData("podman", "podman run -d --name web --label=io.systemagent.deployment=web --restart=always --pod=app --env=A=1 --env=B=x y --volume=/a:/b img:1")]
    [InlineData("docker", "docker run -d --name web --label=io.systemagent.deployment=web --restart=always --publish=8080:80 --publish=8443:443 --env=A=1 --env=B=x y --volume=/a:/b img:1")]
    public async Task RunContainer_ExpandsListPlaceholders(string templateId, string expected)
    {
        var runner = new FakeRunner();
        var store = new CommandTemplateStore(Path.Combine(AppContext.BaseDirectory, "CommandTemplates"), null, NullLogger<CommandTemplateStore>.Instance);
        var provider = new TemplateContainerRuntimeProvider(
            new TemplateCommandExecutor(store.Templates.Single(t => t.Id == templateId), runner), new RuntimeInfo(templateId, "1", templateId));

        var pod = templateId == "podman" ? "app" : null;
        IReadOnlyList<string> ports = pod is null ? ["8080:80", "8443:443"] : [];
        await provider.RunContainerAsync(new ContainerRunSpec("web", "img:1", "always", pod, ports, ["A=1", "B=x y"], ["/a:/b"]));
        Assert.Equal(expected, Assert.Single(runner.Calls));

        if (templateId == "docker")
            await Assert.ThrowsAsync<CapabilityUnavailableException>(() =>
                provider.RunContainerAsync(new ContainerRunSpec("web", "img:1", "always", "app", [], [], [])));
    }

    /// <summary>コンテナ・イメージを記憶する疑似ランタイム。</summary>
    private sealed class FakeRuntime : IContainerRuntimeResolver, IContainerRuntimeProvider
    {
        private int _nextId;
        public List<ContainerInfo> Containers { get; } = [];
        public List<string> Pulled { get; } = [];
        public HashSet<string> LocalImages { get; } = [];
        public HashSet<string> CrashingTags { get; } = [];
        public HashSet<string> FailingRunTags { get; } = [];
        public HashSet<string> MissingTags { get; } = [];
        public int StopCount { get; private set; }
        public ContainerRunSpec? LastRun { get; private set; }

        public Task<IContainerRuntimeProvider> ResolveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IContainerRuntimeProvider>(this);

        public RuntimeInfo Runtime { get; } = new("podman", "5.0", "podman");
        public bool SupportsPods => true;

        public Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ContainerInfo>>([.. Containers]);

        public Task StartContainerAsync(string id, CancellationToken cancellationToken = default) => Set(id, c => c with { State = ContainerState.Running });

        public Task StopContainerAsync(string id, CancellationToken cancellationToken = default)
        {
            StopCount++;
            return Set(id, c => c with { State = ContainerState.Exited });
        }

        public Task RestartContainerAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RemoveContainerAsync(string id, CancellationToken cancellationToken = default)
        {
            var container = Containers.Single(c => c.Id == id);
            if (container.State == ContainerState.Running) throw new CommandFailedException("rm", 2, "container is running");
            Containers.Remove(container);
            return Task.CompletedTask;
        }

        public Task<string> GetContainerLogsAsync(string id, int tail, CancellationToken cancellationToken = default) => Task.FromResult("crash log\n");

        public Task<IReadOnlyList<ImageInfo>> ListImagesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task TagImageAsync(string image, string target, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PushImageAsync(string image, bool tlsVerify = true, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PullImageAsync(string image, bool tlsVerify = true, CancellationToken cancellationToken = default)
        {
            if (MissingTags.Contains(image.Split(':')[^1])) throw new CommandFailedException("pull", 125, "manifest unknown");
            Pulled.Add(image);
            LocalImages.Add(image);
            return Task.CompletedTask;
        }

        public Task RemoveImageAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string> LoadImageAsync(string archivePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PodInfo>> ListPodsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PodInfo>>([]);
        public Task LoginAsync(string registry, string username, string password, bool tlsVerify = true, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LogoutAsync(string registry, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>再起動を繰り返す（確認時点では動作中に見える）タグ。</summary>
        public HashSet<string> RestartLoopTags { get; } = [];

        public Task<int?> GetRestartCountAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult<int?>(RestartLoopTags.Contains(Containers.Single(c => c.Id == id).Image.Split(':')[^1]) ? 3 : 0);

        public Task<bool> ImageExistsAsync(string image, CancellationToken cancellationToken = default) => Task.FromResult(LocalImages.Contains(image));

        public Task RenameContainerAsync(string id, string newName, CancellationToken cancellationToken = default)
        {
            if (Containers.Any(c => c.Name == newName)) throw new CommandFailedException("rename", 125, "name in use");
            return Set(id, c => c with { Name = newName });
        }

        public Task RunContainerAsync(ContainerRunSpec spec, CancellationToken cancellationToken = default)
        {
            LastRun = spec;
            var tag = spec.Image.Split(':')[^1];
            if (FailingRunTags.Contains(tag)) throw new CommandFailedException("run", 126, "port is already allocated");
            if (Containers.Any(c => c.Name == spec.Name)) throw new CommandFailedException("run", 125, "name in use");
            var state = CrashingTags.Contains(tag) ? ContainerState.Exited : ContainerState.Running;
            Containers.Add(new ContainerInfo($"c{++_nextId}", spec.Name, spec.Image, state, state.ToString(), DateTimeOffset.UtcNow, spec.Pod, false));
            return Task.CompletedTask;
        }

        private Task Set(string id, Func<ContainerInfo, ContainerInfo> change)
        {
            var index = Containers.FindIndex(c => c.Id == id);
            Containers[index] = change(Containers[index]);
            return Task.CompletedTask;
        }
    }
}
