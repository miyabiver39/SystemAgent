using SystemAgent.Core.CapabilityProviders;

namespace SystemAgent.Core.Deploy;

/// <summary>イメージの取得方針。Missing はローカルに無い場合だけ取得する（エアギャップ環境で取り込んだイメージをそのまま使える）。</summary>
public enum PullPolicy
{
    Missing,
    Always,
}

/// <summary>
/// デプロイするアプリ（1コンテナ）の定義。ADR-024。
/// </summary>
/// <param name="Name">コンテナ名。</param>
/// <param name="Image">タグを除いたイメージ（例: registry.example.com/app/web）。タグはデプロイ時に指定する。</param>
/// <param name="HealthCheckSeconds">起動後、この秒数だけ待って動作中なら成功とする。</param>
public sealed record DeploymentSpec(
    string Name,
    string Image,
    IReadOnlyList<string> Ports,
    IReadOnlyList<string> Environment,
    IReadOnlyList<string> Volumes,
    string? Pod = null,
    string Restart = "always",
    PullPolicy Pull = PullPolicy.Missing,
    int HealthCheckSeconds = 10);

/// <param name="Action">deploy / rollback。</param>
public sealed record DeploymentEvent(
    DateTimeOffset At, string User, string Action, string? FromTag, string ToTag, bool Success, string Message);

/// <summary>画面・CLI向けの状態。秘密情報らしい環境変数の値は伏せる。</summary>
public sealed record DeploymentView(
    DeploymentSpec Spec,
    string? CurrentTag,
    string? PreviousTag,
    DateTimeOffset? LastDeployedAt,
    ContainerState? State,
    string? RunningImage,
    bool Deploying,
    IReadOnlyList<DeploymentEvent> History);

/// <summary>デプロイに失敗した（元のコンテナに戻した）。</summary>
public sealed class DeploymentFailedException(string message) : Exception(message);
