using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SystemAgent.Core.Health;
using SystemAgent.Infrastructure;
using SystemAgent.Infrastructure.Persistence;

namespace SystemAgent.Core.Tests;

public sealed class DatabaseConnectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sa-db-tests-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private ServiceProvider Services()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["SecretStore:Path"] = Path.Combine(_dir, "secrets") })
            .Build();
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddInfrastructure(configuration, _dir);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Unconfigured_FailsImmediatelyWithoutConnecting()
    {
        await using var provider = Services();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var watch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<DatabaseNotConfiguredException>(() => db.Users.AnyAsync());
        Assert.False(await scope.ServiceProvider.GetRequiredService<IDatabaseStatus>().CanConnectAsync());

        // 到達しない接続先の接続タイムアウト（5秒）を待たない
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"{watch.Elapsed}");
    }
}
