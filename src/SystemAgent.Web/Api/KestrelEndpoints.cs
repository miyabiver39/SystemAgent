using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using SystemAgent.Infrastructure.Cluster;

namespace SystemAgent.Web.Api;

/// <summary>
/// 待ち受けの構成。
/// <list type="bullet">
/// <item>WebUI/API: 設定の urls（ASPNETCORE_URLS / Urls）どおり。HTTP平文可（ADR-004）。</item>
/// <item>ノード間通信: Cluster:Port（既定5443、0で無効）のTLS。サーバー証明書はノード証明書（クラスタ参加後に有効）、
/// クライアント証明書は任意で受け取り、NodeCertificateAuthenticationHandlerで検証する。</item>
/// </list>
/// Kestrelはコードで待ち受けを1つでも指定すると urls 設定を無視するため、urls もここで明示的に待ち受ける。
/// </summary>
public static class KestrelEndpoints
{
    public static void Configure(KestrelServerOptions kestrel, IConfiguration configuration)
    {
        var urls = (configuration["urls"] ?? "http://localhost:5000")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var url in urls) Listen(kestrel, url);

        var clusterPort = configuration.GetValue("Cluster:Port", ClusterEndpointSettings.DefaultPort);
        if (clusterPort <= 0) return;

        kestrel.ListenAnyIP(clusterPort, listen => listen.UseHttps(https =>
        {
            var identity = kestrel.ApplicationServices.GetRequiredService<ClusterIdentity>();
            // 参加前はnullとなりハンドシェイクは失敗する（参加すると再起動なしで有効になる）
            https.ServerCertificateSelector = (_, _) => identity.Current?.NodeCertificate;
            https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
            https.ClientCertificateValidation = (_, _, _) => true;
        }));
    }

    private static void Listen(KestrelServerOptions kestrel, string url)
    {
        var address = BindingAddress.Parse(url);
        void Configure(ListenOptions options)
        {
            if (address.Scheme == Uri.UriSchemeHttps) options.UseHttps();
        }

        switch (address.Host)
        {
            case "localhost":
                kestrel.ListenLocalhost(address.Port, Configure);
                break;
            case "0.0.0.0" or "*" or "+" or "[::]" or "::":
                kestrel.ListenAnyIP(address.Port, Configure);
                break;
            default:
                if (!IPAddress.TryParse(address.Host.Trim('[', ']'), out var ip))
                    throw new InvalidOperationException($"待ち受けアドレスにはIPアドレス・localhost・0.0.0.0のいずれかを指定してください: {url}");
                kestrel.Listen(ip, address.Port, Configure);
                break;
        }
    }
}
