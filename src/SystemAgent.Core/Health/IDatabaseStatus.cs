namespace SystemAgent.Core.Health;

public interface IDatabaseStatus
{
    Task<bool> CanConnectAsync(CancellationToken cancellationToken = default);
}
