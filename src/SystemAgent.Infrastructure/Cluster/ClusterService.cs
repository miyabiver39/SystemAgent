using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SystemAgent.Core.CapabilityProviders;
using SystemAgent.Core.Contracts;
using SystemAgent.Core.Nodes;
using SystemAgent.Infrastructure.Persistence;
using SystemAgent.Infrastructure.Persistence.Entities;
using SystemAgent.Core.Errors;

namespace SystemAgent.Infrastructure.Cluster;

/// <summary>このノード自身の名前・到達アドレス・ノード間通信ポート（Cluster:NodeName / AdvertiseAddress / Port）。</summary>
public sealed class ClusterEndpointSettings(IConfiguration configuration)
{
    public const int DefaultPort = 5443;

    public string NodeName => configuration["Cluster:NodeName"] is { Length: > 0 } name ? name : Environment.MachineName;

    public int Port => configuration.GetValue("Cluster:Port", DefaultPort);

    /// <summary>他ノードからこのノードに到達するアドレス。未設定ならデフォルトゲートウェイを持つインターフェースのIPv4。</summary>
    public string AdvertiseAddress => configuration["Cluster:AdvertiseAddress"] is { Length: > 0 } address ? address : DetectAddress();

    private static string DetectAddress()
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(n => n.GetIPProperties())
            .OrderByDescending(p => p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
            .SelectMany(p => p.UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.Address.ToString());
        return candidates.FirstOrDefault() ?? "127.0.0.1";
    }
}

/// <summary>
/// クラスタ（自己CA・ノード参加）の操作（基本設計書 4.1節、ADR-018）。
/// </summary>
public sealed class ClusterService(
    AppDbContext db, ClusterIdentity identity, ClusterEndpointSettings endpoint, IEnvironmentDetector detector,
    TimeProvider time, ILogger<ClusterService> logger)
{
    private const string CaCertificateSetting = "ca.certificate";
    private const string CaNodeIdSetting = "ca.nodeId";
    private const string ClusterNameSetting = "cluster.name";

    public ClusterStatusResponse GetStatus()
    {
        var current = identity.Current;
        return new ClusterStatusResponse(
            Joined: current is not null,
            ClusterName: current?.ClusterName,
            NodeId: current?.NodeId,
            NodeName: endpoint.NodeName,
            IsCa: current?.IsCa ?? false,
            CaFingerprint: current is null ? null : Pki.Fingerprint(current.CaCertificate),
            CertificateNotAfter: current?.NodeCertificate.NotAfter,
            AdvertiseAddress: endpoint.AdvertiseAddress,
            ClusterPort: endpoint.Port);
    }

    /// <summary>このノードをクラスタCAとして初期化し、自分自身をノードとして登録する。</summary>
    public async Task<ClusterStatusResponse> InitializeAsync(string clusterName, CancellationToken cancellationToken)
    {
        if (identity.Current is not null) throw new ClusterStateException("このノードは既にクラスタに参加しています。");
        if (await db.ClusterSettings.AnyAsync(s => s.Key == CaCertificateSetting, cancellationToken))
            throw new ClusterStateException("このデータベースには既にクラスタCAがあります。参加トークンで参加してください。");

        var now = time.GetUtcNow();
        using var ca = Pki.CreateCa(clusterName, now);
        var nodeId = Guid.NewGuid();
        var (csr, nodeKey) = Pki.CreateNodeCsr(endpoint.NodeName);
        using var nodeCert = Pki.SignNodeCsr(ca, csr, nodeId, endpoint.NodeName, [endpoint.AdvertiseAddress, endpoint.NodeName], now);

        await RegisterNodeAsync(nodeId, endpoint.NodeName, endpoint.AdvertiseAddress, endpoint.Port,
            await CurrentOsAsync(cancellationToken), nodeCert.NotAfter, cancellationToken);
        db.ClusterSettings.AddRange(
            new ClusterSettingEntity { Key = CaCertificateSetting, Value = ca.ExportCertificatePem() },
            new ClusterSettingEntity { Key = CaNodeIdSetting, Value = nodeId.ToString() },
            new ClusterSettingEntity { Key = ClusterNameSetting, Value = clusterName });
        await db.SaveChangesAsync(cancellationToken);

        identity.Save(clusterName, nodeId, nodeCert.ExportCertificatePem(), nodeKey,
            ca.ExportCertificatePem(), ca.GetECDsaPrivateKey()!.ExportPkcs8PrivateKeyPem());
        logger.LogInformation("クラスタ '{Cluster}' のCAとして初期化しました（ノードID {NodeId}）。", clusterName, nodeId);
        return GetStatus();
    }

    /// <summary>参加トークンを発行する。どのノードからでも発行でき、接続先はCAノードになる。</summary>
    public async Task<JoinTokenResponse> CreateJoinTokenAsync(string actor, int validMinutes, CancellationToken cancellationToken)
    {
        var caPem = await SettingAsync(CaCertificateSetting, cancellationToken)
            ?? throw new ClusterStateException("クラスタが初期化されていません。先にCAを初期化してください。");
        var caNodeId = Guid.Parse((await SettingAsync(CaNodeIdSetting, cancellationToken))!);
        var caNode = await db.Nodes.AsNoTracking().SingleOrDefaultAsync(n => n.Id == caNodeId, cancellationToken)
            ?? throw new ClusterStateException("CAノードの登録情報が見つかりません。");

        var secret = JoinToken.NewSecret();
        var now = time.GetUtcNow();
        var token = new JoinTokenEntity
        {
            Id = Guid.NewGuid(),
            SecretHash = JoinToken.Hash(secret),
            CreatedBy = actor,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(validMinutes),
        };
        db.JoinTokens.Add(token);
        await db.SaveChangesAsync(cancellationToken);

        using var ca = X509Certificate2.CreateFromPem(caPem);
        var caUrl = $"https://{FormatHost(caNode.IpAddress)}:{caNode.ClusterPort}";
        return new JoinTokenResponse(new JoinToken(caUrl, Pki.Fingerprint(ca), secret).Encode(), token.ExpiresAt, caUrl);
    }

    /// <summary>CAノード側: トークンを検証し、CSRに署名してノードを登録する。</summary>
    public async Task<EnrollResponse> EnrollAsync(EnrollRequest request, CancellationToken cancellationToken)
    {
        var current = identity.Current;
        if (current is not { IsCa: true }) throw new ClusterStateException("このノードはクラスタCAではありません。");

        var now = time.GetUtcNow();
        var hash = JoinToken.Hash(request.Secret);
        var token = await db.JoinTokens.SingleOrDefaultAsync(t => t.SecretHash == hash, cancellationToken);
        if (token is null || token.UsedAt is not null || token.ExpiresAt < now)
            throw new UnauthorizedAccessException("参加トークンが無効か、期限切れ・使用済みです。");
        if (await db.Nodes.AnyAsync(n => n.HostName == request.NodeName, cancellationToken))
            throw new ClusterStateException($"ノード名 '{request.NodeName}' は既に登録されています。再登録する場合は先にノードを削除してください。");

        var nodeId = Guid.NewGuid();
        using var ca = identity.LoadCaWithPrivateKey();
        X509Certificate2 cert;
        try
        {
            cert = Pki.SignNodeCsr(ca, request.CsrPem, nodeId, request.NodeName, [request.Address, request.NodeName], now);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            throw new ArgumentException("証明書署名要求（CSR）が不正です。", ex);
        }
        using var _ = cert;

        token.UsedAt = now;
        token.UsedByNodeId = nodeId;
        await RegisterNodeAsync(nodeId, request.NodeName, request.Address, request.ClusterPort, request.Os, cert.NotAfter, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("ノード '{Node}'（{Address}:{Port}）をクラスタに登録しました。", request.NodeName, request.Address, request.ClusterPort);
        return new EnrollResponse(nodeId, current.ClusterName, cert.ExportCertificatePem(), ca.ExportCertificatePem());
    }

    /// <summary>
    /// 新規ノード側: トークンに埋め込まれたCAの指紋で接続先を検証しながら、CSRを送って証明書を受け取る。
    /// </summary>
    public async Task<ClusterStatusResponse> JoinAsync(string tokenText, CancellationToken cancellationToken)
    {
        if (identity.Current is not null) throw new ClusterStateException("このノードは既にクラスタに参加しています。");
        var token = JoinToken.Decode(tokenText.Trim());

        // 1. CA証明書を取得し、トークンの指紋と一致するか確認（この時点ではサーバー証明書を検証できないため取得のみ）
        X509Certificate2 ca;
        using (var probe = new HttpClient(new SocketsHttpHandler
               {
                   SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
               }) { BaseAddress = new Uri(token.CaUrl), Timeout = TimeSpan.FromSeconds(30) })
        {
            var caPem = await probe.GetStringAsync("api/cluster/ca", cancellationToken);
            ca = X509Certificate2.CreateFromPem(caPem);
        }
        if (!string.Equals(Pki.Fingerprint(ca), token.CaFingerprint, StringComparison.OrdinalIgnoreCase))
            throw new ClusterStateException("CAの指紋が参加トークンと一致しません。接続先が正しいか確認してください。");

        // 2. 以降はCAで署名されたサーバー証明書でなければ接続しない
        using var client = new HttpClient(new SocketsHttpHandler
        {
            SslOptions =
            {
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    certificate is not null && Pki.ValidateNodeCertificate(certificate, ca, NodeCertificateUsage.Server) is not null,
            },
        }) { BaseAddress = new Uri(token.CaUrl), Timeout = TimeSpan.FromSeconds(60) };

        var (csr, key) = Pki.CreateNodeCsr(endpoint.NodeName);
        var response = await client.PostAsJsonAsync("api/cluster/enroll",
            new EnrollRequest(token.Secret, endpoint.NodeName, endpoint.AdvertiseAddress, endpoint.Port, await CurrentOsAsync(cancellationToken), csr),
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new ClusterStateException($"CAノードが参加を拒否しました（{(int)response.StatusCode}）: {body}");
        }
        var enrolled = (await response.Content.ReadFromJsonAsync<EnrollResponse>(cancellationToken))!;

        using var issued = X509Certificate2.CreateFromPem(enrolled.NodeCertificatePem);
        // 受け取った証明書はサーバー・クライアントの両方に使う
        if (Pki.ValidateNodeCertificate(issued, ca, NodeCertificateUsage.Server) != enrolled.NodeId
            || Pki.ValidateNodeCertificate(issued, ca, NodeCertificateUsage.Client) != enrolled.NodeId)
            throw new ClusterStateException("CAノードから受け取った証明書が不正です。");

        identity.Save(enrolled.ClusterName, enrolled.NodeId, enrolled.NodeCertificatePem, key, ca.ExportCertificatePem(), null);
        logger.LogInformation("クラスタ '{Cluster}' に参加しました（ノードID {NodeId}）。", enrolled.ClusterName, enrolled.NodeId);
        return GetStatus();
    }

    /// <summary>クラスタCAのノードID（中央DBに記録したもの）。未初期化ならnull。</summary>
    public async Task<Guid?> GetCaNodeIdAsync(CancellationToken cancellationToken) =>
        Guid.TryParse(await SettingAsync(CaNodeIdSetting, cancellationToken), out var id) ? id : null;

    public async Task<string?> GetCaCertificatePemAsync(CancellationToken cancellationToken) =>
        identity.Current?.CaCertificate.ExportCertificatePem() ?? await SettingAsync(CaCertificateSetting, cancellationToken);

    private async Task RegisterNodeAsync(Guid id, string name, string address, int port, OsInfo os, DateTime notAfter,
        CancellationToken cancellationToken)
    {
        // 手動登録された同名ノードがあれば置き換える
        await db.Nodes.Where(n => n.HostName == name).ExecuteDeleteAsync(cancellationToken);
        db.Nodes.Add(new NodeEntity
        {
            Id = id,
            HostName = name,
            IpAddress = address,
            ClusterPort = port,
            OsDistribution = os.Distribution,
            OsVersion = os.Version,
            OsArchitecture = os.Architecture,
            Role = NodeRole.Managed,
            RegisteredAt = time.GetUtcNow(),
            CertificateNotAfter = new DateTimeOffset(notAfter),
        });
    }

    private async Task<OsInfo> CurrentOsAsync(CancellationToken cancellationToken)
    {
        var environment = await detector.DetectAsync(cancellationToken: cancellationToken);
        return new OsInfo(environment.OsId, environment.OsVersion, environment.Architecture);
    }

    private Task<string?> SettingAsync(string key, CancellationToken cancellationToken) =>
        db.ClusterSettings.Where(s => s.Key == key).Select(s => s.Value).SingleOrDefaultAsync(cancellationToken);

    private static string FormatHost(string address) => address.Contains(':') ? $"[{address}]" : address;
}
