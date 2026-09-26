namespace SystemAgent.Core.CapabilityProviders;

/// <summary>
/// コンテナランタイム(Podman/Docker)を抽象化するインターフェース。
/// 実装差分はOS/バージョンごとのコマンドテンプレートに外出しする（基本設計書 7章）。
/// </summary>
public interface IContainerRuntimeProvider
{
    Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(CancellationToken cancellationToken = default);

    Task PullImageAsync(string image, CancellationToken cancellationToken = default);

    Task DeployImageAsync(string image, string containerName, CancellationToken cancellationToken = default);
}

public sealed record ContainerInfo(string Id, string Name, string Image, string Status);
