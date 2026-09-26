using SystemAgent.Core.CapabilityProviders;
using SystemAgent.Infrastructure.CapabilityProviders.Templates;

namespace SystemAgent.Infrastructure.CapabilityProviders.Network;

/// <summary>コマンドテンプレートに従ってホストネットワークの状態を取得する（参照のみ）。</summary>
public sealed class TemplateNetworkProvider(TemplateCommandExecutor executor) : INetworkProvider
{
    public async Task<NetworkStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var interfaces = NetworkOutputParsers.Interfaces[executor.Parser("listInterfaces")!](
            (await executor.RunAsync("listInterfaces", null, cancellationToken)).StandardOutput);

        var routes = new List<RouteInfo>();
        foreach (var command in new[] { "listRoutes", "listRoutes6" }.Where(executor.Has))
        {
            routes.AddRange(NetworkOutputParsers.Routes[executor.Parser(command)!](
                (await executor.RunAsync(command, null, cancellationToken)).StandardOutput));
        }

        var resolvConf = executor.Setting("resolvConf");
        var (dns, search) = File.Exists(resolvConf)
            ? NetworkOutputParsers.ParseResolvConf(await File.ReadAllTextAsync(resolvConf, cancellationToken))
            : ([], []);

        return new NetworkStatus(interfaces, routes, dns, search);
    }
}
