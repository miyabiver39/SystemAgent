using Microsoft.EntityFrameworkCore;
using SystemAgent.Core.Nodes;
using SystemAgent.Infrastructure.Persistence;
using SystemAgent.Infrastructure.Persistence.Entities;

namespace SystemAgent.Infrastructure.Nodes;

public sealed class NodeService(AppDbContext db) : INodeService
{
    public async Task<IReadOnlyList<NodeInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        var entities = await db.Nodes.AsNoTracking().OrderBy(n => n.HostName).ToListAsync(cancellationToken);
        return entities.Select(ToModel).ToList();
    }

    public async Task<NodeInfo?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await db.Nodes.AsNoTracking().SingleOrDefaultAsync(n => n.Id == id, cancellationToken);
        return entity is null ? null : ToModel(entity);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
        await db.Nodes.Where(n => n.Id == id).ExecuteDeleteAsync(cancellationToken) > 0;

    private static NodeInfo ToModel(NodeEntity e) => new(
        e.Id, e.HostName, e.IpAddress, e.ClusterPort,
        new OsInfo(e.OsDistribution, e.OsVersion, e.OsArchitecture), e.Role, e.RegisteredAt, e.CertificateNotAfter);
}
