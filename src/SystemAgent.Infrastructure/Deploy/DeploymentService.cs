using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SystemAgent.Core.CapabilityProviders;
using SystemAgent.Core.Deploy;
using SystemAgent.Core.Security;
using SystemAgent.Infrastructure.CapabilityProviders.Containers;
using SystemAgent.Core.Errors;
using SystemAgent.Infrastructure.Security;

namespace SystemAgent.Infrastructure.Deploy;

/// <summary>
/// 自社アプリ（コンテナ）のイメージ更新・デプロイ（ADR-024）。
/// 定義はノードごとにローカルの暗号化シークレットへ保存する（環境変数に秘密情報を含み得るため。中央DB停止中もデプロイ・ロールバックできる）。
/// <para>手順: イメージ取得 → 既存コンテナを "&lt;名前&gt;-previous" に改名して停止 → 新しいコンテナを起動 → 一定時間後に動作中か確認。
/// 失敗したら新しいコンテナを削除し、元のコンテナを戻して起動する。</para>
/// </summary>
public sealed class DeploymentService(
    ILocalSecretStore secrets, IContainerRuntimeResolver resolver, ImageTransferService transfer, ILogger<DeploymentService> logger)
{
    private const string PreviousSuffix = DeploymentSpecs.PreviousSuffix;
    private const int MaxHistory = 30;

    private readonly SecretJsonStore<List<Record>> _store = new(secrets, "deploy.apps");
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
        spec = DeploymentSpecs.Validate(spec);
        _store.Update(current =>
        {
            var records = current ?? [];
            var existing = records.FirstOrDefault(r => r.Spec.Name == spec.Name);
            spec = DeploymentSpecs.RestoreMaskedSecrets(spec, existing?.Spec);
            records.RemoveAll(r => r.Spec.Name == spec.Name);
            records.Add(existing is null ? new Record(spec, null, null, null, []) : existing with { Spec = spec });
            return records;
        });
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
        _store.Update(current =>
        {
            var records = current ?? [];
            records.RemoveAll(r => r.Spec.Name == name);
            return records;
        });
        return true;
    }

    public async Task<DeploymentView> DeployAsync(string name, string tag, string user, CancellationToken cancellationToken = default)
    {
        if (!ContainerNames.IsValidTag(tag)) throw new ArgumentException($"タグが不正です: {tag}");
        return await RunAsync(name, tag, user, "deploy", cancellationToken);
    }

    /// <summary>直前にデプロイしていたタグに戻す。</summary>
    public async Task<DeploymentView> RollbackAsync(string name, string user, CancellationToken cancellationToken = default)
    {
        var record = Find(name) ?? throw NotDefined(name);
        var previous = record.PreviousTag ?? throw new ClusterStateException("戻せるバージョンがありません（デプロイ履歴が1件以下です）。");
        return await RunAsync(name, previous, user, "rollback", cancellationToken);
    }

    private async Task<DeploymentView> RunAsync(string name, string tag, string user, string action, CancellationToken cancellationToken)
    {
        var record = Find(name) ?? throw NotDefined(name);
        using var _ = await AcquireAsync(name);
        var spec = record.Spec;
        var image = $"{spec.Image}:{tag}";
        var runtime = await resolver.ResolveAsync(cancellationToken);

        try
        {
            await DeployCoreAsync(runtime, spec, image, cancellationToken);
        }
        catch (Exception ex) when (ex is CommandFailedException or DeploymentFailedException or CapabilityUnavailableException
                                       or OperationCanceledException)
        {
            // コマンドの標準エラーは進捗表示の後にエラーが出るため、最後の行を履歴に残す
            var message = ex switch
            {
                CommandFailedException command =>
                    command.StandardError.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? ex.Message,
                OperationCanceledException => "要求が取り消された（接続切れ・タイムアウト等）ため中断しました。切り替え後であれば元のコンテナに戻しています。",
                _ => ex.Message,
            };
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
            await transfer.PullAsync(runtime, image, cancellationToken);
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
        catch (Exception ex)
        {
            // 取り消し（接続切れ・タイムアウト）を含むどの失敗でも元に戻す。戻さないと旧コンテナが退避・停止したまま残り、サービスが止まる
            await RestoreAsync(runtime, spec.Name, current, ex);
            throw;
        }

        // 4. 成功したら退避したコンテナを削除（イメージはロールバック用に残す）。切り替えは済んでいるため取り消しは受け付けない
        if (current is not null) await runtime.RemoveContainerAsync(current.Id, CancellationToken.None);
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
        var spec = DeploymentSpecs.MaskSecrets(record.Spec);
        var deploying = _running.TryGetValue(record.Spec.Name, out var semaphore) && semaphore.CurrentCount == 0;
        return new DeploymentView(spec, record.CurrentTag, record.PreviousTag, record.LastDeployedAt,
            container?.State, container?.Image, deploying, record.History);
    }

    private static NotFoundException NotDefined(string name) => new($"アプリ {name} は定義されていません。");

    private Record? Find(string name) => Load().FirstOrDefault(r => r.Spec.Name == name);

    private Record Update(string name, Func<Record, Record> change)
    {
        Record? updated = null;
        _store.Update(current =>
        {
            var records = current ?? [];
            var index = records.FindIndex(r => r.Spec.Name == name);
            if (index < 0) throw NotDefined(name);
            var changed = change(records[index]);
            records[index] = updated = changed with { History = [.. changed.History.Take(MaxHistory)] };
            return records;
        });
        return updated!;
    }

    private List<Record> Load() => _store.Load() ?? [];
}
