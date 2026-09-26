using SystemAgent.Core.Health;
using SystemAgent.Infrastructure.Persistence;

namespace SystemAgent.Infrastructure.Health;

public sealed class DatabaseStatus(AppDbContext db) : IDatabaseStatus
{
    public Task<bool> CanConnectAsync(CancellationToken cancellationToken = default) =>
        db.Database.CanConnectAsync(cancellationToken);
}
