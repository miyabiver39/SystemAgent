using SystemAgent.Core.Contracts;
using SystemAgent.Core.Deploy;

namespace SystemAgent.Client;

// アプリのデプロイ
public sealed partial class ApiClient
{
    public Task<List<DeploymentView>> GetDeploymentsAsync(CancellationToken cancellationToken = default) =>
        SendAsync<List<DeploymentView>>(HttpMethod.Get, "api/deployments", null, authorize: true, cancellationToken);

    public Task<DeploymentView> GetDeploymentAsync(string name, CancellationToken cancellationToken = default) =>
        SendAsync<DeploymentView>(HttpMethod.Get, $"api/deployments/{Uri.EscapeDataString(name)}", null, authorize: true, cancellationToken);

    public Task SaveDeploymentAsync(DeploymentSpec spec, CancellationToken cancellationToken = default) =>
        SendAndDisposeAsync(HttpMethod.Put, $"api/deployments/{Uri.EscapeDataString(spec.Name.Trim())}", spec, authorize: true, cancellationToken);

    public Task RemoveDeploymentAsync(string name, bool removeContainer, CancellationToken cancellationToken = default) =>
        SendAndDisposeAsync(HttpMethod.Delete, $"api/deployments/{Uri.EscapeDataString(name)}?removeContainer={removeContainer}", null, authorize: true, cancellationToken);

    public Task<DeploymentView> DeployAsync(string name, string tag, CancellationToken cancellationToken = default) =>
        SendAsync<DeploymentView>(HttpMethod.Post, $"api/deployments/{Uri.EscapeDataString(name)}/deploy", new DeployRequest(tag), authorize: true, cancellationToken);

    public Task<DeploymentView> RollbackAsync(string name, CancellationToken cancellationToken = default) =>
        SendAsync<DeploymentView>(HttpMethod.Post, $"api/deployments/{Uri.EscapeDataString(name)}/rollback", null, authorize: true, cancellationToken);
}
