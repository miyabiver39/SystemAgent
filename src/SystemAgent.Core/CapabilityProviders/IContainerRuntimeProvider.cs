namespace SystemAgent.Core.CapabilityProviders;

/// <summary>
/// コンテナランタイム(Podman/Docker)を抽象化するインターフェース（基本設計書 7章、ADR-005）。
/// ランタイム・OS・バージョンごとの差分はコマンドテンプレート(JSON)に外出しし、このインターフェースは固定する。
/// </summary>
public interface IContainerRuntimeProvider
{
    /// <summary>使用中のランタイム（podman / docker）とそのバージョン。</summary>
    RuntimeInfo Runtime { get; }

    /// <summary>Pod（ネットワークグループ）に対応しているか。Podmanのみtrue。</summary>
    bool SupportsPods { get; }

    Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(CancellationToken cancellationToken = default);

    Task StartContainerAsync(string id, CancellationToken cancellationToken = default);

    Task StopContainerAsync(string id, CancellationToken cancellationToken = default);

    Task RestartContainerAsync(string id, CancellationToken cancellationToken = default);

    Task RemoveContainerAsync(string id, CancellationToken cancellationToken = default);

    Task<string> GetContainerLogsAsync(string id, int tail, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ImageInfo>> ListImagesAsync(CancellationToken cancellationToken = default);

    Task PullImageAsync(string image, CancellationToken cancellationToken = default);

    Task RemoveImageAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>イメージアーカイブ(tar)を読み込む。エアギャップ環境向け。ランタイムの出力（読み込んだイメージ名等）を返す。</summary>
    Task<string> LoadImageAsync(string archivePath, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PodInfo>> ListPodsAsync(CancellationToken cancellationToken = default);

    /// <summary>レジストリにログインする。パスワードは標準入力で渡す（プロセス一覧に出さない）。</summary>
    Task LoginAsync(string registry, string username, string password, CancellationToken cancellationToken = default);

    Task LogoutAsync(string registry, CancellationToken cancellationToken = default);
}

public sealed record RuntimeInfo(string Name, string Version, string TemplateId);

public enum ContainerState
{
    Unknown,
    Created,
    Running,
    Paused,
    Restarting,
    Exited,
    Removing,
    Dead,
}

/// <param name="IsInfra">PodmanのPodインフラコンテナ（Podのネットワーク名前空間を保持する内部コンテナ）か。</param>
public sealed record ContainerInfo(
    string Id,
    string Name,
    string Image,
    ContainerState State,
    string Status,
    DateTimeOffset? CreatedAt,
    string? Pod,
    bool IsInfra);

public sealed record ImageInfo(string Id, IReadOnlyList<string> Tags, long? SizeBytes, DateTimeOffset? CreatedAt);

public sealed record PodInfo(string Id, string Name, string Status, int ContainerCount, DateTimeOffset? CreatedAt);
