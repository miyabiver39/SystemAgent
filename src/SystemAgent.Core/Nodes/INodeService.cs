namespace SystemAgent.Core.Nodes;

/// <summary>
/// 登録済みノードの参照・削除。ノードの登録はクラスタ参加（参加トークン + 証明書発行）でのみ行う（ADR-018）。
/// </summary>
public interface INodeService
{
    Task<IReadOnlyList<NodeInfo>> ListAsync(CancellationToken cancellationToken = default);

    Task<NodeInfo?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
