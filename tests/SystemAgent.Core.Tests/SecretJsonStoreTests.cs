using Microsoft.Extensions.Logging.Abstractions;
using SystemAgent.Core.Ha;
using SystemAgent.Infrastructure.Security;

namespace SystemAgent.Core.Tests;

public sealed class SecretJsonStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sa-secretjson-tests-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private LocalSecretStore Secrets() => new(Path.Combine(_dir, "secrets"), NullLogger<LocalSecretStore>.Instance);

    private sealed record Sample(string Name, HaReturnMode Mode, List<string> Items);

    [Fact]
    public void SaveAndLoad_RoundTrip_AndMissingIsNull()
    {
        var store = new SecretJsonStore<Sample>(Secrets(), "sample");
        Assert.Null(store.Load());

        store.Save(new Sample("a", HaReturnMode.Auto, ["x"]));
        var loaded = new SecretJsonStore<Sample>(Secrets(), "sample").Load()!;
        Assert.Equal(("a", HaReturnMode.Auto, "x"), (loaded.Name, loaded.Mode, Assert.Single(loaded.Items)));
    }

    [Fact]
    public void Load_ReadsEnumsSavedAsNumbers()
    {
        // 共通化前のHA設定は列挙型を数値で保存していた
        var secrets = Secrets();
        secrets.SetSecret("sample", """{"name":"a","mode":1,"items":[]}""");
        Assert.Equal(HaReturnMode.Manual, new SecretJsonStore<Sample>(secrets, "sample").Load()!.Mode);
    }

    [Fact]
    public async Task Update_IsSerialized()
    {
        var store = new SecretJsonStore<List<string>>(Secrets(), "list");
        await Task.WhenAll(Enumerable.Range(0, 50).Select(i => Task.Run(() => store.Update(current => [.. current ?? [], $"item{i}"]))));
        Assert.Equal(50, store.Load()!.Count);
    }

    [Fact]
    public async Task Update_IsSerializedAcrossInstancesWithSameName()
    {
        // サービスごとに別のインスタンスを作っても、同じ名前の更新は失われない
        var secrets = Secrets();
        await Task.WhenAll(Enumerable.Range(0, 50).Select(i => Task.Run(() =>
            new SecretJsonStore<List<string>>(secrets, "list").Update(current => [.. current ?? [], $"item{i}"]))));
        Assert.Equal(50, new SecretJsonStore<List<string>>(secrets, "list").Load()!.Count);
    }
}
