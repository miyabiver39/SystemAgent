using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SystemAgent.Core.CapabilityProviders;
using SystemAgent.Infrastructure.Backup;
using SystemAgent.Infrastructure.CapabilityProviders.Templates;
using SystemAgent.Infrastructure.Commands;
using SystemAgent.Infrastructure.Persistence;
using SystemAgent.Infrastructure.Security;
using FakeRunner = SystemAgent.Core.Tests.CommandTemplateTests.FakeRunner;
using SystemAgent.Core.Errors;

namespace SystemAgent.Core.Tests;

public sealed class BackupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sa-backup-tests-" + Guid.NewGuid());

    public BackupTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private BackupService Service(FakeRunner runner, int retention = 7, int rateKBps = 0)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backup:Directory"] = Path.Combine(_dir, "backups"),
            ["Backup:Retention"] = retention.ToString(),
            ["Backup:RateLimitKBps"] = rateKBps.ToString(),
            ["ConnectionStrings:Default"] = "Server=db.local;Port=3306;Database=systemagent;User=sa;Password=p\"w\\d",
        }).Build();
        var secrets = new LocalSecretStore(Path.Combine(_dir, "secrets"), NullLogger<LocalSecretStore>.Instance);
        var store = new CommandTemplateStore(Path.Combine(AppContext.BaseDirectory, "CommandTemplates"), null, NullLogger<CommandTemplateStore>.Instance);
        var resolver = new CapabilityTemplateResolver(new FixedDetector(), store, runner, configuration);
        return new BackupService(resolver, runner, new DatabaseConnection(secrets, configuration), configuration,
            TimeProvider.System, NullLogger<BackupService>.Instance);
    }

    [Fact]
    public async Task Create_WritesCompressedDump_AndPassesCredentialsViaOptionFile()
    {
        var runner = new FakeRunner { StreamOutput = Encoding.UTF8.GetBytes("-- dump\nCREATE TABLE x (id int);\n") };
        var service = Service(runner);

        var created = await service.CreateAsync(CancellationToken.None);

        Assert.Matches(@"^systemagent-\d{8}-\d{6}\.sql\.gz$", created.Name);
        await using var gzip = new GZipStream(service.OpenRead(created.Name), CompressionMode.Decompress);
        Assert.Equal("-- dump\nCREATE TABLE x (id int);\n", await new StreamReader(gzip).ReadToEndAsync());

        var call = Assert.Single(runner.Calls);
        Assert.StartsWith("mariadb-dump --defaults-extra-file=", call);
        Assert.EndsWith("--databases systemagent", call);
        Assert.DoesNotContain("p\"w", call); // パスワードはコマンドライン引数に出さない
        var optionFile = call.Split(' ')[1]["--defaults-extra-file=".Length..];
        Assert.False(File.Exists(optionFile)); // 一時オプションファイルは消える
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "backups"), "*.partial"));
    }

    [Fact]
    public async Task Create_Failure_LeavesNoPartialFile()
    {
        var runner = new FakeRunner { Result = new CommandResult(2, "", "Access denied") };
        var service = Service(runner);

        var ex = await Assert.ThrowsAsync<CommandFailedException>(() => service.CreateAsync(CancellationToken.None));
        Assert.Contains("Access denied", ex.Message);
        Assert.Empty(service.List());
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "backups")));
    }

    [Fact]
    public async Task Prune_KeepsNewestAccordingToRetention()
    {
        var service = Service(new FakeRunner(), retention: 2);
        var dir = Path.Combine(_dir, "backups");
        Directory.CreateDirectory(dir);
        foreach (var name in new[] { "systemagent-20260101-000000.sql.gz", "systemagent-20260102-000000.sql.gz", "systemagent-20260103-000000.sql.gz" })
            await File.WriteAllTextAsync(Path.Combine(dir, name), "x");
        await File.WriteAllTextAsync(Path.Combine(dir, "unrelated.txt"), "keep");

        service.Prune();

        Assert.Equal(["systemagent-20260103-000000.sql.gz", "systemagent-20260102-000000.sql.gz"], service.List().Select(b => b.Name));
        Assert.True(File.Exists(Path.Combine(dir, "unrelated.txt")));
    }

    [Fact]
    public async Task Restore_StreamsDecompressedSqlToClient()
    {
        var runner = new FakeRunner { StreamOutput = Encoding.UTF8.GetBytes("INSERT INTO t VALUES (1);") };
        var service = Service(runner);
        var created = await service.CreateAsync(CancellationToken.None);

        await service.RestoreAsync(created.Name, CancellationToken.None);

        Assert.StartsWith("mariadb --defaults-extra-file=", runner.Calls[^1]);
        Assert.Equal("INSERT INTO t VALUES (1);", Encoding.UTF8.GetString(runner.ReceivedInput));
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("systemagent-20260101-000000.sql.gz/../../x")]
    [InlineData("backup.sql.gz")]
    public void InvalidNames_AreRejected(string name)
    {
        var service = Service(new FakeRunner());
        Assert.Throws<ArgumentException>(() => service.OpenRead(name));
        Assert.Throws<ArgumentException>(() => service.Delete(name));
    }

    [Fact]
    public async Task ThrottledStream_LimitsRate()
    {
        using var target = new MemoryStream();
        await using var throttled = new ThrottledStream(target, bytesPerSecond: 200_000);
        var watch = Stopwatch.StartNew();

        await throttled.WriteAsync(new byte[100_000]);

        // 200KB/秒で100KB → 約0.5秒
        Assert.InRange(watch.Elapsed.TotalSeconds, 0.4, 3);
        Assert.Equal(100_000, target.Length);
    }

    [Fact]
    public void ThrottledStream_SyncWriteIsLimitedToo()
    {
        using var target = new MemoryStream();
        using var throttled = new ThrottledStream(target, bytesPerSecond: 200_000);
        var watch = Stopwatch.StartNew();

        throttled.Write(new byte[100_000]);

        Assert.InRange(watch.Elapsed.TotalSeconds, 0.4, 3);
        Assert.Equal(100_000, target.Length);
    }

    [Fact]
    public async Task ThrottledStream_IdleTimeDoesNotAllowUnlimitedBurst()
    {
        using var target = new MemoryStream();
        await using var throttled = new ThrottledStream(target, bytesPerSecond: 500_000);
        await throttled.WriteAsync(new byte[1]);

        // 転送が止まっていた2秒分を後から一気に流さない（余裕は最大1秒分 = 500KB）
        await Task.Delay(TimeSpan.FromSeconds(2));
        var watch = Stopwatch.StartNew();
        await throttled.WriteAsync(new byte[1_000_000]);

        // 1MB - 余裕500KB = 500KB 分（約1秒）待つ
        Assert.InRange(watch.Elapsed.TotalSeconds, 0.8, 3);
    }

    [Fact]
    public async Task ThrottledStream_ZeroMeansUnlimited()
    {
        using var target = new MemoryStream();
        await using var throttled = new ThrottledStream(target, bytesPerSecond: 0);
        var watch = Stopwatch.StartNew();
        await throttled.WriteAsync(new byte[5_000_000]);
        Assert.True(watch.Elapsed.TotalSeconds < 1);
    }

    [Theory]
    [InlineData("2026-09-27T02:00:00+09:00", "03:00", 1)]
    [InlineData("2026-09-27T03:00:00+09:00", "03:00", 24)]
    [InlineData("2026-09-27T04:00:00+09:00", "03:00", 23)]
    public void Scheduler_NextDelay(string now, string at, int expectedHours) =>
        Assert.Equal(TimeSpan.FromHours(expectedHours), BackupScheduler.NextDelay(DateTimeOffset.Parse(now), TimeOnly.Parse(at)));

    [Fact]
    public void Scheduler_NextDelay_TimerFiredSlightlyEarly_DoesNotRunTwiceSameDay()
    {
        // 03:00 の実行がタイマーの誤差で数ミリ秒早く終わった直後
        var now = DateTimeOffset.Parse("2026-09-27T02:59:59.995+09:00");
        var at = TimeOnly.Parse("03:00");

        Assert.Equal(TimeSpan.FromMilliseconds(5), BackupScheduler.NextDelay(now, at));
        Assert.Equal(TimeSpan.FromMilliseconds(5) + TimeSpan.FromDays(1), BackupScheduler.NextDelay(now, at, DateOnly.Parse("2026-09-27")));
    }

    private sealed class FixedDetector : IEnvironmentDetector
    {
        public Task<HostEnvironment> DetectAsync(bool refresh = false, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HostEnvironment("h", "almalinux", ["rhel"], "9.8", "Alma", "x86_64", [new ToolInfo("mariadb-dump", "10.5.29")]));
    }
}
