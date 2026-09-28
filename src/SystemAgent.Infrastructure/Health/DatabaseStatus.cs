using SystemAgent.Core.Health;
using SystemAgent.Infrastructure.Persistence;

namespace SystemAgent.Infrastructure.Health;

public sealed class DatabaseStatus(AppDbContext db, DatabaseConnection connection) : IDatabaseStatus
{
    public async Task<bool> CanConnectAsync(CancellationToken cancellationToken = default) =>
        connection.IsConfigured && await db.Database.CanConnectAsync(cancellationToken);
}
