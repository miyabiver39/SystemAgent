using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace SystemAgent.Web.Client;

/// <summary>
/// WebUIが自プロセスのWebAPIを呼ぶ際の接続先（ADR-007）。ブラウザから見えるURL(VIPやNginx経由)ではなく、
/// サーバー自身が待ち受けているアドレスを使う。WebUi:ApiBaseAddress で明示指定も可能。
/// </summary>
public sealed class ApiBaseAddress(IServer server, IConfiguration configuration)
{
    private Uri? _value;

    public Uri Value => _value ??= Resolve();

    private Uri Resolve()
    {
        if (configuration["WebUi:ApiBaseAddress"] is { Length: > 0 } configured) return new Uri(configured);

        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
        var address = addresses.FirstOrDefault(a => a.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            ?? addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("サーバーの待ち受けアドレスを取得できません。WebUi:ApiBaseAddress を設定してください。");

        var builder = new UriBuilder(address.Replace("://+", "://localhost").Replace("://*", "://localhost"));
        if (builder.Host is "0.0.0.0" or "[::]" or "::") builder.Host = "localhost";
        return builder.Uri;
    }
}
