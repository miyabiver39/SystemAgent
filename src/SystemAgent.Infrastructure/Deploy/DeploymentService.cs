using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SystemAgent.Core.CapabilityProviders;
using SystemAgent.Core.Deploy;
using SystemAgent.Core.Security;
using SystemAgent.Infrastructure.CapabilityProviders.Containers;
using SystemAgent.Infrastructure.Cluster;

namespace SystemAgent.Infrastructure.Deploy;

/// <summary>
/// 自社アプリ（コンテナ）のイメージ更新・デプロイ（ADR-024）。
/// 定義はノードごとにローカルの暗号化シークレットへ保存する（環境変数に秘密情報を含み得るため。中央DB停止中もデプロイ・ロールバックできる）。
/// <para>手順: イメージ取得 → 既存コンテナを "&lt;名前&gt;-previous" に改名して停止 → 新しいコンテナを起動 → 一定時間後に動作中か確認。
/// 失敗したら新しいコンテナを削除し、元のコンテナを戻して起動する。</para>
/// </summary>
public sealed partial class DeploymentService(
    ILocalSecretStore secrets, IContainerRuntimeResolver resolver, RegistryService registries, ILogger<DeploymentService> logger)
{
    private const string SecretName = "deploy.apps";
    private const string PreviousSuffix = "-previous";
    private const int MaxHistory = 30;
    public const string Masked = "********";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private readonly Lock _lock = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _running = new();

    private sealed record Record(
        DeploymentSpec Spec, string? CurrentTag, string? PreviousTag, DateTimeOffset? LastDeployedAt, List<DeploymentEvent> History);

    public async Task<IReadOnlyList<DeploymentView>> ListAsync(CancellationToken cancellationToken = default)
    {
        var records = Load();
        if (records.Count == 0) return [];
        var containers = await (await resolver.ResolveAsync(cancellationToken)).ListContainersAsync(cancellationToken);
        return records.OrderBy(r => r.Spec.Name).Select(r => View(r, containers)).ToList();
    }

    public async Task<DeploymentView?> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        if (Find(name) is not { } record) return null;
        var containers = await (await resolver.ResolveAsync(cancellationToken)).ListContainersAsync(cancellationToken);
        return View(record, containers);
    }

    /// <summary>定義を追加・更新する（デプロイはしない）。伏せ字のままの環境変数は保存済みの値を引き継ぐ。</summary>
    public DeploymentSpec Save(DeploymentSpec spec)
    {
        spec = Validate(spec);
        lock (_lock)
        {
            var records = Load();
            var existing = records.FirstOrDefault(r => r.Spec.Name == spec.Name);
            var environment = spec.Environment.Select(e =>
            {
                var (key, value) = SplitEnv(e);
                if (value != Masked) return e;
                return existing?.Spec.Environment.FirstOrDefault(old => SplitEnv(old).Key == key)
                    ?? throw new ArgumentException($"環境変数 {key} の値を入力してください。");
            }).ToList();
            spec = spec with { Environment = environment };
            records.RemoveAll(r => r.Spec.Name == spec.Name);
            records.Add(existing is null ? new Record(spec, null, null, null, []) : existing with { Spec = spec });
            Store(records);
        }
        return spec;
    }

    /// <summary>定義を削除する。removeContainer ならコンテナ（退避中のものを含む）も停止・削除する。</summary>
    public async Task<bool> RemoveAsync(string name, bool removeContainer, CancellationToken cancellationToken = default)
    {
        if (Find(name) is null) return false;
        using var _ = await AcquireAsync(name);
        if (removeContainer)
        {
            var runtime = await resolver.ResolveAsync(cancellationToken);
            var containers = await runtime.ListContainersAsync(cancellationToken);
            foreach (var container in containers.Where(c => c.Name == name || c.Name == name + PreviousSuffix))
                await StopAndRemoveAsync(runtime, container, cancellationToken);
        }
        lock (_lock)
        {
            var records = Load();
            records.RemoveAll(r => r.Spec.Name == name);
            Store(records);
        }
        return true;
    }

    public async Task<DeploymentView> DeployAsync(string name, string tag, string user, CancellationToken cancellationToken = default)
    {
        if (!TagPattern().IsMatch(tag)) throw new ArgumentException($"タグが不正です: {tag}");
        return await RunAsync(name, tag, user, "deploy", cancellationToken);
    }

    /// <summary>直前にデプロイしていたタグに戻す。</summary>
    public async Task<DeploymentView> RollbackAsync(string name, string user, CancellationToken cancellationToken = default)
    {
        var record = Find(name) ?? throw new KeyNotFoundException(name);
        var previous = record.PreviousTag ?? throw new ClusterStateException("戻せるバージョンがありません（デプロイ履歴が1件以下です）。");
        return await RunAsync(name, previous, user, "rollback", cancellationToken);
    }

    private async Task<DeploymentView> RunAsync(string name, string tag, string user, string action, CancellationToken cancellationToken)
    {
        var record = Find(name) ?? throw new KeyNotFoundException(name);
        using var _ = await AcquireAsync(name);
        var spec = record.Spec;
        var image = $"{spec.Image}:{tag}";
        var runtime = await resolver.ResolveAsync(cancellationToken);

        try
        {
            await DeployCoreAsync(runtime, spec, image, cancellationToken);
        }
        catch (Exception ex) when (ex is CommandFailedException or DeploymentFailedException or CapabilityUnavailableException)
        {
            // コマンドの標準エラーは進捗表示の後にエラーが出るため、最後の行を履歴に残す
            var message = ex is CommandFailedException command
                ? command.StandardError.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? ex.Message
                : ex.Message;
            Update(name, r => r with
            {
                History = [new DeploymentEvent(DateTimeOffset.UtcNow, user, action, r.CurrentTag, tag, false, message), .. r.History],
            });
            logger.LogWarning("デプロイに失敗しました: {Name} {Image}: {Message}", name, image, message);
            throw;
        }

        var updated = Update(name, r => r with
        {
            CurrentTag = tag,
            PreviousTag = r.CurrentTag == tag ? r.PreviousTag : r.CurrentTag,
            LastDeployedAt = DateTimeOffset.UtcNow,
            History = [new DeploymentEvent(DateTimeOffset.UtcNow, user, action, r.CurrentTag, tag, true, $"{image} を起動しました。"), .. r.History],
        });
        logger.LogInformation("デプロイしました: {Name} {Image}", name, image);
        return View(updated, await runtime.ListContainersAsync(cancellationToken));
    }

    private async Task DeployCoreAsync(IContainerRuntimeProvider runtime, DeploymentSpec spec, string image, CancellationToken cancellationToken)
    {
        // 1. イメージ（切替前に取得しておき、停止時間を短くする）
        if (spec.Pull == PullPolicy.Always || !await runtime.ImageExistsAsync(image, cancellationToken))
        {
            var tlsVerify = await registries.EnsureLoginAsync(runtime, image, cancellationToken);
            await runtime.PullImageAsync(image, tlsVerify, cancellationToken);
        }

        // 2. 既存コンテナを退避（前回の退避が残っていれば削除）
        var containers = await runtime.ListContainersAsync(cancellationToken);
        var previousName = spec.Name + PreviousSuffix;
        if (containers.FirstOrDefault(c => c.Name == previousName) is { } stale)
            await StopAndRemoveAsync(runtime, stale, cancellationToken);
        var current = containers.FirstOrDefault(c => c.Name == spec.Name);
        if (current is not null)
        {
            if (current.State is ContainerState.Running or ContainerState.Restarting or ContainerState.Paused)
                await runtime.StopContainerAsync(current.Id, cancellationToken);
            await runtime.RenameContainerAsync(current.Id, previousName, cancellationToken);
        }

        // 3. 新しいコンテナを起動し、動作を確認する。失敗したら元に戻す
        try
        {
            await runtime.RunContainerAsync(new ContainerRunSpec(
                spec.Name, image, spec.Restart, spec.Pod, spec.Ports, spec.Environment, spec.Volumes), cancellationToken);
            if (spec.HealthCheckSeconds > 0) await Task.Delay(TimeSpan.FromSeconds(spec.HealthCheckSeconds), cancellationToken);
            // 再起動ポリシーで再起動を繰り返している場合も、確認した瞬間は動作中に見えるため再起動回数も見る
            var started = (await runtime.ListContainersAsync(cancellationToken)).FirstOrDefault(c => c.Name == spec.Name);
            var restarts = started is null ? null : await runtime.GetRestartCountAsync(started.Id, cancellationToken);
            if (started?.State != ContainerState.Running || restarts > 0)
            {
                var logs = started is null ? "" : await TryLogsAsync(runtime, started.Id, cancellationToken);
                var reason = restarts > 0 ? $"{restarts} 回再起動しました" : $"状態: {started?.Status ?? "なし"}";
                throw new DeploymentFailedException(
                    $"{image} が起動後 {spec.HealthCheckSeconds} 秒の間に正常に動作し続けませんでした（{reason}）。"
                    + (logs.Length > 0 ? $"\nログ（末尾）:\n{logs}" : ""));
            }
        }
        catch (Exception ex) when (ex is CommandFailedException or DeploymentFailedException)
        {
            await RestoreAsync(runtime, spec.Name, current, ex);
            throw;
        }

        // 4. 成功したら退避したコンテナを削除（イメージはロールバック用に残す）
        if (current is not null) await runtime.RemoveContainerAsync(current.Id, cancellationToken);
    }

    /// <summary>
    /// 新しいコンテナを削除し、退避したコンテナを元の名前に戻して起動する。
    /// 呼び出し元の取り消し（接続切れ等）で復旧が中断されないよう、取り消しは受け付けない。
    /// </summary>
    private async Task RestoreAsync(IContainerRuntimeProvider runtime, string name, ContainerInfo? previous, Exception cause)
    {
        try
        {
            var failed = (await runtime.ListContainersAsync()).FirstOrDefault(c => c.Name == name);
            if (failed is not null) await StopAndRemoveAsync(runtime, failed, CancellationToken.None);
            if (previous is not null)
            {
                await runtime.RenameContainerAsync(previous.Id, name);
                if (previous.State is ContainerState.Running or ContainerState.Restarting or ContainerState.Paused)
                    await runtime.StartContainerAsync(previous.Id);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "デプロイ失敗後の復旧にも失敗しました: {Name}（元の原因: {Cause}）", name, cause.Message);
            throw new DeploymentFailedException(
                $"デプロイに失敗し、元のコンテナへの復旧にも失敗しました。コンテナ画面で {name} / {name}{PreviousSuffix} を確認してください。原因: {cause.Message} / 復旧: {ex.Message}");
        }
    }

    private static async Task StopAndRemoveAsync(IContainerRuntimeProvider runtime, ContainerInfo container, CancellationToken cancellationToken)
    {
        if (container.State is ContainerState.Running or ContainerState.Restarting or ContainerState.Paused)
            await runtime.StopContainerAsync(container.Id, cancellationToken);
        await runtime.RemoveContainerAsync(container.Id, cancellationToken);
    }

    private static async Task<string> TryLogsAsync(IContainerRuntimeProvider runtime, string id, CancellationToken cancellationToken)
    {
        try
        {
            return (await runtime.GetContainerLogsAsync(id, 30, cancellationToken)).Trim();
        }
        catch (CommandFailedException)
        {
            return "";
        }
    }

    private async Task<IDisposable> AcquireAsync(string name)
    {
        var semaphore = _running.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));
        if (!await semaphore.WaitAsync(0)) throw new ClusterStateException($"{name} は現在デプロイ中です。完了してから操作してください。");
        return new Releaser(semaphore);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        public void Dispose() => semaphore.Release();
    }

    private DeploymentView View(Record record, IReadOnlyList<ContainerInfo> containers)
    {
        var container = containers.FirstOrDefault(c => c.Name == record.Spec.Name);
        var spec = record.Spec with
        {
            Environment = record.Spec.Environment.Select(e => SplitEnv(e) is var (key, _) && IsSecretKey(key) ? $"{key}={Masked}" : e).ToList(),
        };
        var deploying = _running.TryGetValue(record.Spec.Name, out var semaphore) && semaphore.CurrentCount == 0;
        return new DeploymentView(spec, record.CurrentTag, record.PreviousTag, record.LastDeployedAt,
            container?.State, container?.Image, deploying, record.History);
    }

    /// <summary>名前から秘密情報とみなす環境変数（値を画面・APIに出さない）。</summary>
    public static bool IsSecretKey(string key) => SecretKeyPattern().IsMatch(key);

    private static (string Key, string Value) SplitEnv(string entry)
    {
        var index = entry.IndexOf('=');
        return index < 0 ? (entry, "") : (entry[..index], entry[(index + 1)..]);
    }

    /// <summary>入力を検証し、空白の除去・空要素の削除をしたものを返す。</summary>
    public static DeploymentSpec Validate(DeploymentSpec spec)
    {
        var name = spec.Name.Trim();
        if (!NamePattern().IsMatch(name) || name.EndsWith(PreviousSuffix, StringComparison.Ordinal))
            throw new ArgumentException($"コンテナ名が不正です（英数字と _ . - 、63文字以内、末尾 {PreviousSuffix} は不可）: {spec.Name}");
        var image = spec.Image.Trim();
        var lastSegment = image[(image.LastIndexOf('/') + 1)..];
        if (!ImagePattern().IsMatch(image) || lastSegment.Contains(':'))
            throw new ArgumentException($"イメージはタグなしで指定してください（例: registry.example.com/app/web）: {spec.Image}");
        if (spec.Restart is not ("no" or "always" or "unless-stopped" or "on-failure"))
            throw new ArgumentException("再起動ポリシーは no / always / unless-stopped / on-failure のいずれかです。");
        var pod = string.IsNullOrWhiteSpace(spec.Pod) ? null : spec.Pod.Trim();
        if (pod is not null && !NamePattern().IsMatch(pod)) throw new ArgumentException($"Pod名が不正です: {spec.Pod}");
        if (spec.HealthCheckSeconds is < 0 or > 600) throw new ArgumentException("動作確認の待ち時間は0〜600秒です。");

        var ports = Clean(spec.Ports, PortPattern(), "ポート公開（例: 8080:80、127.0.0.1:8080:80/tcp）");
        if (pod is not null && ports.Count > 0) throw new ArgumentException("Podに参加する場合、ポートはPod側で公開してください。");
        var environment = Clean(spec.Environment, EnvPattern(), "環境変数（KEY=VALUE）");
        if (environment.GroupBy(e => SplitEnv(e).Key).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
            throw new ArgumentException($"環境変数 {duplicate.Key} が重複しています。");
        var volumes = Clean(spec.Volumes, VolumePattern(), "ボリューム（/ホストのパス:/コンテナのパス[:ro] または 名前:/パス）");
        return spec with { Name = name, Image = image, Pod = pod, Ports = ports, Environment = environment, Volumes = volumes };
    }

    private static List<string> Clean(IReadOnlyList<string> values, Regex pattern, string label)
    {
        var cleaned = values.Select(v => v.Trim()).Where(v => v.Length > 0).ToList();
        if (cleaned.FirstOrDefault(v => v.Length > 4096 || !pattern.IsMatch(v)) is { } invalid)
            throw new ArgumentException($"{label} の指定が不正です: {invalid}");
        return cleaned;
    }

    private Record? Find(string name) => Load().FirstOrDefault(r => r.Spec.Name == name);

    private Record Update(string name, Func<Record, Record> change)
    {
        lock (_lock)
        {
            var records = Load();
            var index = records.FindIndex(r => r.Spec.Name == name);
            if (index < 0) throw new KeyNotFoundException(name);
            var updated = change(records[index]);
            records[index] = updated with { History = [.. updated.History.Take(MaxHistory)] };
            Store(records);
            return records[index];
        }
    }

    private List<Record> Load() =>
        secrets.GetSecret(SecretName) is { } json ? JsonSerializer.Deserialize<List<Record>>(json, Json) ?? [] : [];

    private void Store(List<Record> records) => secrets.SetSecret(SecretName, JsonSerializer.Serialize(records, Json));

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]{0,62}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._/:-]{0,254}$")]
    private static partial Regex ImagePattern();

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"^((\d{1,3}\.){3}\d{1,3}:)?(\d{1,5}(-\d{1,5})?:)?\d{1,5}(-\d{1,5})?(/(tcp|udp|sctp))?$")]
    private static partial Regex PortPattern();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*=[^\x00-\x08\x0A-\x1F\x7F]*$")]
    private static partial Regex EnvPattern();

    [GeneratedRegex(@"^(/[^:\x00-\x1F]*|[A-Za-z0-9][A-Za-z0-9_.-]*):/[^:\x00-\x1F]*(:[A-Za-z,]+)?$")]
    private static partial Regex VolumePattern();

    [GeneratedRegex(@"(PASS|SECRET|TOKEN|KEY|CREDENTIAL)", RegexOptions.IgnoreCase)]
    private static partial Regex SecretKeyPattern();
}
