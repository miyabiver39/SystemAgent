using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SystemAgent.Core.Auditing;
using SystemAgent.Core.Ha;
using SystemAgent.Core.Nodes;
using SystemAgent.Infrastructure.CapabilityProviders.Templates;
using SystemAgent.Infrastructure.Cluster;
using SystemAgent.Infrastructure.Persistence;
using SystemAgent.Core.Errors;

namespace SystemAgent.Infrastructure.Ha;

/// <summary>
/// Keepalived + DBの昇格・降格（基本設計書 4.2節、ADR-022）。
/// keepalived の notify から呼ばれ、MASTERになったらローカルDBを昇格、それ以外は読み取り専用にする。
/// 元マスターの復帰は、自動モードなら現マスター（VIP）のレプリカとして再参加させ、手動モードなら承認待ちにする。
/// </summary>
public sealed class HaService(
    HaSettingsStore settingsStore, HaStateStore stateStore, DbRoleManager db, CapabilityTemplateResolver resolver,
    ClusterIdentity identity, ClusterEndpointSettings endpoint, IServiceScopeFactory scopes, TimeProvider time,
    IConfiguration configuration, ILogger<HaService> logger) : IAsyncDisposable
{
    public const string Capability = "keepalived";

    public HaSettings? Settings => settingsStore.Load();

    public HaState State => stateStore.Load();

    public string HookTokenPath => stateStore.HookTokenPath;

    public void EnsureHookToken() => stateStore.EnsureHookToken();

    public bool VerifyHookToken(string? token) => stateStore.VerifyHookToken(token);

    public async Task<DbRoleStatus?> GetDbStatusAsync(CancellationToken cancellationToken) =>
        Settings?.LocalDb is { } localDb ? await db.GetStatusAsync(localDb, cancellationToken) : null;

    /// <summary>設定を保存する。パスワード類が空なら既存の値を引き継ぐ。</summary>
    public HaSettings Save(HaSettings incoming)
    {
        KeepalivedConfig.Validate(incoming);
        var current = Settings;
        var merged = incoming with
        {
            AuthPass = incoming.AuthPass ?? current?.AuthPass,
            LocalDb = string.IsNullOrEmpty(incoming.LocalDb) ? current?.LocalDb : incoming.LocalDb,
            ReplicationPassword = string.IsNullOrEmpty(incoming.ReplicationPassword) ? current?.ReplicationPassword : incoming.ReplicationPassword,
        };
        settingsStore.Save(merged);
        return merged;
    }

    /// <summary>
    /// keepalived.conf を生成して適用する。keepalived -t で検証してから置き換え、再起動に失敗したら元に戻す。
    /// 無効化の場合は keepalived を停止する。
    /// </summary>
    public async Task ApplyAsync(CancellationToken cancellationToken)
    {
        var settings = Settings ?? throw new ClusterStateException("HA設定が保存されていません。");
        var (executor, _) = await resolver.ResolveAsync(Capability, "HA:Tool", ["keepalived"], "keepalived", cancellationToken);
        if (!settings.Enabled)
        {
            await executor.RunAsync("stopService", null, cancellationToken);
            Record("HAを無効化し、keepalivedを停止しました。");
            return;
        }

        stateStore.EnsureHookToken();
        var configFile = executor.Setting("configFile");
        var content = KeepalivedConfig.Generate(settings, endpoint.NodeName);

        var candidate = configFile + ".systemagent.new";
        await WriteAsync(candidate, content, cancellationToken);
        try
        {
            await executor.RunAsync("configTest", new Dictionary<string, string> { ["file"] = candidate }, cancellationToken);
        }
        finally
        {
            File.Delete(candidate);
        }

        var original = File.Exists(configFile) ? await File.ReadAllTextAsync(configFile, cancellationToken) : null;
        if (original is not null && !File.Exists(configFile + ".systemagent.orig"))
            await WriteAsync(configFile + ".systemagent.orig", original, cancellationToken);
        await WriteAsync(configFile, content, cancellationToken);
        try
        {
            await executor.RunAsync("restartService", null, cancellationToken);
        }
        catch (CommandFailedException)
        {
            if (original is null) File.Delete(configFile);
            else await WriteAsync(configFile, original, CancellationToken.None);
            logger.LogWarning("keepalived の再起動に失敗したため設定を元に戻しました。");
            throw;
        }
        Record($"keepalived の設定を適用しました（VIP {settings.VirtualIp}、VRID {settings.VirtualRouterId}、優先度 {settings.Priority}）。");
    }

    /// <summary>keepalived の notify から呼ばれる。</summary>
    public async Task NotifyAsync(VrrpState state, CancellationToken cancellationToken)
    {
        // 状態が変わったら、BACKUP になったときに予約した再参加の判断は取り消す
        CancelPendingRejoin();
        var previous = State.State;
        stateStore.Update(s => s with { State = state, Since = time.GetUtcNow() });
        Record($"VRRPの状態が {previous} から {state} になりました。");

        var settings = Settings;
        if (settings?.LocalDb is not { } localDb)
        {
            Record("このノードのDB管理用接続が未設定のため、DBの昇格・降格は行いません。");
            return;
        }

        if (state == VrrpState.Master)
        {
            await db.PromoteAsync(localDb, cancellationToken);
            stateStore.Update(s => s with { RejoinPending = false });
            Record("ローカルDBをマスターに昇格しました（複製停止・書き込み許可）。");
            await UpdateRolesAsync(NodeRole.Master, cancellationToken);
            return;
        }

        // MASTER以外では書き込みを禁止する（VIPを失った旧マスターが書き込みを受け付けるスプリットブレインを防ぐ）
        await db.SetReadOnlyAsync(localDb, cancellationToken);
        Record("ローカルDBを読み取り専用にしました。");
        if (state != VrrpState.Backup) return;

        // keepalived は起動直後に必ず一度 BACKUP を経由し、他にMASTERがいなければ数秒後にMASTERになる。
        // すぐに再参加すると本来のマスターが自分のレプリカのレプリカになってしまうため、猶予後もBACKUPなら判断する
        ScheduleRejoinEvaluation();
    }

    /// <summary>BACKUPになってから再参加を判断するまでの猶予（HA:RejoinDelaySeconds、既定15秒）。</summary>
    private TimeSpan RejoinDelay => TimeSpan.FromSeconds(configuration.GetValue("HA:RejoinDelaySeconds", 15));

    private readonly Lock _rejoinLock = new();
    private CancellationTokenSource? _pendingRejoin;
    private Task _pendingRejoinTask = Task.CompletedTask;

    /// <summary>
    /// 猶予の後に再参加を判断する処理を予約する。状態が変わったとき（CancelPendingRejoin）とアプリ終了時（DisposeAsync）に取り消す。
    /// 取り消せるのは猶予の待機中だけで、判断・再参加を始めたら中途半端な状態で止めないよう最後まで行う。
    /// </summary>
    private void ScheduleRejoinEvaluation()
    {
        lock (_rejoinLock)
        {
            var cts = new CancellationTokenSource();
            _pendingRejoin = cts;
            _pendingRejoinTask = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(RejoinDelay, time, cts.Token);
                    lock (_rejoinLock)
                    {
                        if (cts.IsCancellationRequested) return;
                        _pendingRejoin = null; // 以降は取り消さない
                    }
                    if (State.State == VrrpState.Backup) await EvaluateRejoinAsync(CancellationToken.None);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    // 状態の変化・アプリの終了で取り消された
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "レプリカとしての再参加の判断に失敗しました。");
                    Record($"レプリカとしての再参加に失敗しました: {ex.Message}");
                }
                finally
                {
                    cts.Dispose();
                }
            }, CancellationToken.None);
        }
    }

    private void CancelPendingRejoin()
    {
        lock (_rejoinLock)
        {
            _pendingRejoin?.Cancel();
            _pendingRejoin = null;
        }
    }

    /// <summary>アプリ終了時: 待機中の再参加判断を取り消し、実行中の再参加は終わるまで待つ（DB操作を途中で打ち切らない）。</summary>
    public async ValueTask DisposeAsync()
    {
        Task pending;
        lock (_rejoinLock)
        {
            _pendingRejoin?.Cancel();
            _pendingRejoin = null;
            pending = _pendingRejoinTask;
        }
        if (await Task.WhenAny(pending, Task.Delay(ShutdownWait)) != pending)
            logger.LogWarning("レプリカとしての再参加の処理が {Seconds} 秒以内に終わらないまま終了します。", ShutdownWait.TotalSeconds);
    }

    private static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(30);

    private async Task EvaluateRejoinAsync(CancellationToken cancellationToken)
    {
        if (Settings is not { LocalDb: { } localDb } settings) return;

        var status = await db.GetStatusAsync(localDb, cancellationToken);
        var replicatingFromSource = status.IsReplica && status.SourceHost == settings.SourceHost && status.SourcePort == settings.ReplicationSourcePort;
        if (replicatingFromSource) return;

        if (status.PrerequisiteProblems is { Count: > 0 } problems)
        {
            // 前提を満たさない構成で自動的に再参加するとデータを失う恐れがあるため、自動モードでも承認待ちにする
            stateStore.Update(s => s with { RejoinPending = true });
            Record($"前提条件を満たしていないため、自動の再参加を行いません: {string.Join(" / ", problems)}");
            return;
        }

        if (settings.ReturnMode == HaReturnMode.Auto)
        {
            await RejoinAsync(settings, localDb, cancellationToken);
        }
        else
        {
            stateStore.Update(s => s with { RejoinPending = true });
            Record($"レプリカとしての再参加（{settings.SourceHost}:{settings.ReplicationSourcePort}）は管理者の承認待ちです（手動復帰モード）。");
        }
    }

    /// <summary>手動復帰モードでの再参加の承認。</summary>
    public async Task ApproveRejoinAsync(CancellationToken cancellationToken)
    {
        var settings = Settings ?? throw new ClusterStateException("HA設定が保存されていません。");
        if (settings.LocalDb is not { } localDb) throw new ClusterStateException("このノードのDB管理用接続が未設定です。");
        if (State.State == VrrpState.Master) throw new ClusterStateException("このノードは現在マスター（VIP保持）のため、レプリカにはできません。");
        await RejoinAsync(settings, localDb, cancellationToken);
    }

    private async Task RejoinAsync(HaSettings settings, string localDb, CancellationToken cancellationToken)
    {
        if (settings.ReplicationUser is not { Length: > 0 } user || settings.ReplicationPassword is not { Length: > 0 } password)
            throw new ClusterStateException("レプリケーション用のユーザー・パスワードが未設定です。");

        await db.JoinAsReplicaAsync(localDb, settings.SourceHost, settings.ReplicationSourcePort, user, password, cancellationToken);
        stateStore.Update(s => s with { RejoinPending = false });
        Record($"{settings.SourceHost}:{settings.ReplicationSourcePort} のレプリカとして参加しました。");
        await UpdateRolesAsync(NodeRole.Replica, cancellationToken);
    }

    /// <summary>ノード一覧上の役割を更新する（中央DBに届かない間は諦める）。</summary>
    private async Task UpdateRolesAsync(NodeRole selfRole, CancellationToken cancellationToken)
    {
        if (identity.Current?.NodeId is not { } selfId) return;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (selfRole == NodeRole.Master)
            {
                await context.Nodes.Where(n => n.Role == NodeRole.Master && n.Id != selfId)
                    .ExecuteUpdateAsync(n => n.SetProperty(x => x.Role, NodeRole.Replica), cancellationToken);
            }
            await context.Nodes.Where(n => n.Id == selfId).ExecuteUpdateAsync(n => n.SetProperty(x => x.Role, selfRole), cancellationToken);
            await scope.ServiceProvider.GetRequiredService<IAuditLogger>()
                .LogAsync($"node:{endpoint.NodeName}", "ha.role", selfRole.ToString(), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "ノードの役割（{Role}）を中央DBに記録できませんでした。", selfRole);
        }
    }

    /// <summary>履歴に残す。件数は HaStateStore が上限（最新50件）で切り詰める。</summary>
    private void Record(string message)
    {
        logger.LogInformation("HA: {Message}", message);
        stateStore.Update(s => s with { History = [.. s.History, new HaEvent(time.GetUtcNow(), message)] });
    }

    private static async Task WriteAsync(string path, string content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        // 認証パスワードを含むため root のみ読み書き可能にする
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using (var stream = new FileStream(tmp, options))
        await using (var writer = new StreamWriter(stream))
        {
            await writer.WriteAsync(content.AsMemory(), cancellationToken);
        }
        File.Move(tmp, path, overwrite: true);
    }
}
