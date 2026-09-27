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

    /// <summary>イメージがローカルにあるか（エアギャップ環境ではpullせずに使う）。</summary>
    Task<bool> ImageExistsAsync(string image, CancellationToken cancellationToken = default);

    Task RenameContainerAsync(string id, string newName, CancellationToken cancellationToken = default);

    /// <summary>再起動ポリシーによる再起動の回数。取得できないランタイムではnull。</summary>
    Task<int?> GetRestartCountAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>コンテナを作成して起動する（デプロイ用。ADR-024）。</summary>
    Task RunContainerAsync(ContainerRunSpec spec, CancellationToken cancellationToken = default);
}

/// <param name="Restart">再起動ポリシー（no / always / unless-stopped / on-failure）。</param>
/// <param name="Pod">参加するPod（Podmanのみ）。Podに参加する場合、ポート公開はPod側で行う。</param>
/// <param name="Ports">ポート公開（例: 8080:80、127.0.0.1:8080:80/tcp）。</param>
/// <param name="Environment">環境変数（KEY=VALUE）。</param>
/// <param name="Volumes">ボリューム（/host/path:/container/path[:ro] または 名前付きボリューム:/path）。</param>
public sealed record ContainerRunSpec(
    string Name,
    string Image,
    string Restart,
    string? Pod,
    IReadOnlyList<string> Ports,
    IReadOnlyList<string> Environment,
    IReadOnlyList<string> Volumes);

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
