using Microsoft.EntityFrameworkCore;
using SystemAgent.Core.Nodes;
using SystemAgent.Infrastructure.Persistence;
using SystemAgent.Infrastructure.Persistence.Entities;

namespace SystemAgent.Infrastructure.Nodes;

public sealed class NodeService(AppDbContext db, TimeProvider time) : INodeService
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

    public async Task<NodeInfo?> RegisterAsync(NodeRegistration registration, CancellationToken cancellationToken = default)
    {
        if (await db.Nodes.AnyAsync(n => n.HostName == registration.HostName, cancellationToken)) return null;

        var entity = new NodeEntity
        {
            Id = Guid.NewGuid(),
            HostName = registration.HostName,
            IpAddress = registration.IpAddress,
            OsDistribution = registration.Os.Distribution,
            OsVersion = registration.Os.Version,
            OsArchitecture = registration.Os.Architecture,
            Role = registration.Role,
            RegisteredAt = time.GetUtcNow(),
        };
        db.Nodes.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return ToModel(entity);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
        await db.Nodes.Where(n => n.Id == id).ExecuteDeleteAsync(cancellationToken) > 0;

    private static NodeInfo ToModel(NodeEntity e) =>
        new(e.Id, e.HostName, e.IpAddress, new OsInfo(e.OsDistribution, e.OsVersion, e.OsArchitecture), e.Role, e.RegisteredAt);
}
