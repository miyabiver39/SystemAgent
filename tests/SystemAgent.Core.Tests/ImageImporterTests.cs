using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SystemAgent.Core.Auditing;
using SystemAgent.Core.CapabilityProviders;
using SystemAgent.Core.Errors;
using SystemAgent.Infrastructure.CapabilityProviders.Containers;
using SystemAgent.Infrastructure.CapabilityProviders.Templates;
using FakeRunner = SystemAgent.Core.Tests.CommandTemplateTests.FakeRunner;

namespace SystemAgent.Core.Tests;

public sealed class ImageImporterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sa-import-tests-" + Guid.NewGuid());
    private readonly FakeRunner _runner = new() { Result = new(0, "Loaded image: localhost/app:1\n", "") };

    public ImageImporterTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private ImageImporter Importer(long maxBytes = 1000, long minFreeBytes = 0)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Container:ImportTempPath"] = _dir,
            ["Container:MaxImportSizeBytes"] = maxBytes.ToString(),
            ["Container:ImportMinFreeBytes"] = minFreeBytes.ToString(),
        }).Build();
        var store = new CommandTemplateStore(Path.Combine(AppContext.BaseDirectory, "CommandTemplates"), null, NullLogger<CommandTemplateStore>.Instance);
        var provider = new TemplateContainerRuntimeProvider(
            new TemplateCommandExecutor(store.Templates.Single(t => t.Id == "podman"), _runner), new RuntimeInfo("podman", "5", "podman"));
        return new ImageImporter(new FixedResolver(provider), new NullAudit(), configuration, NullLogger<ImageImporter>.Instance);
    }

    [Fact]
    public async Task Import_WithinLimit_LoadsAndRemovesTempFile()
    {
        var output = await Importer().ImportAsync(new MemoryStream(new byte[1000]), "app.tar", "admin", CancellationToken.None, 1000);

        Assert.Contains("Loaded image", output);
        Assert.StartsWith("podman load -i ", Assert.Single(_runner.Calls));
        Assert.Empty(Directory.EnumerateFiles(_dir));
    }

    [Fact]
    public async Task Import_DeclaredLengthOverLimit_IsRejectedBeforeWriting()
    {
        await Assert.ThrowsAsync<PayloadTooLargeException>(() =>
            Importer().ImportAsync(new MemoryStream(new byte[10]), "app.tar", "admin", CancellationToken.None, 1001));
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task Import_BodyExceedingLimitWithoutDeclaredLength_IsAbortedAndTempFileRemoved()
    {
        await Assert.ThrowsAsync<PayloadTooLargeException>(() =>
            Importer().ImportAsync(new MemoryStream(new byte[200_000]), "app.tar", "admin", CancellationToken.None));

        Assert.Empty(_runner.Calls);
        Assert.Empty(Directory.EnumerateFiles(_dir));
    }

    [Fact]
    public async Task Import_NotEnoughFreeSpace_IsRejected()
    {
        await Assert.ThrowsAsync<InsufficientStorageException>(() =>
            Importer(maxBytes: long.MaxValue, minFreeBytes: long.MaxValue / 2)
                .ImportAsync(new MemoryStream(new byte[10]), "app.tar", "admin", CancellationToken.None, 10));
        Assert.Empty(_runner.Calls);
    }

    private sealed class FixedResolver(IContainerRuntimeProvider provider) : IContainerRuntimeResolver
    {
        public Task<IContainerRuntimeProvider> ResolveAsync(CancellationToken cancellationToken = default) => Task.FromResult(provider);
    }

    private sealed class NullAudit : IAuditLogger
    {
        public Task LogAsync(string actor, string action, string? detail = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
