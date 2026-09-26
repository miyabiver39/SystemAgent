namespace SystemAgent.Core.CapabilityProviders;

/// <summary>
/// dnf/yum/apt等のパッケージマネージャを抽象化するインターフェース。
/// </summary>
public interface IPackageManagerProvider
{
    Task<bool> IsInstalledAsync(string packageName, CancellationToken cancellationToken = default);

    Task<string?> GetInstalledVersionAsync(string packageName, CancellationToken cancellationToken = default);
}
