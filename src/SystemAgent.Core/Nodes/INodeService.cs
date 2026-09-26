namespace SystemAgent.Core.Nodes;

public interface INodeService
{
    Task<IReadOnlyList<NodeInfo>> ListAsync(CancellationToken cancellationToken = default);

    Task<NodeInfo?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <returns>同一ホスト名が登録済みの場合はnull。</returns>
    Task<NodeInfo?> RegisterAsync(NodeRegistration registration, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed record NodeRegistration(string HostName, string IpAddress, OsInfo Os, NodeRole Role);
