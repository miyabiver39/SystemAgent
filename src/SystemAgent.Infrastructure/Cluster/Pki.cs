using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SystemAgent.Infrastructure.Cluster;

/// <summary>
/// クラスタ内の自己CA（基本設計書 4.1節、ADR-018）。鍵はECDSA P-256。
/// ノード証明書はサーバー認証・クライアント認証の両方に使い、SANにノードIDのURIを入れる（なりすまし防止のためノードIDで照合する）。
/// </summary>
public static class Pki
{
    public static readonly TimeSpan CaValidity = TimeSpan.FromDays(365 * 20);
    public static readonly TimeSpan NodeValidity = TimeSpan.FromDays(365 * 5);
    public const string NodeUriScheme = "urn:systemagent:node:";

    private static readonly Oid ServerAuth = new("1.3.6.1.5.5.7.3.1");
    private static readonly Oid ClientAuth = new("1.3.6.1.5.5.7.3.2");

    public static X509Certificate2 CreateCa(string clusterName, DateTimeOffset now)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN=SystemAgent Cluster CA ({clusterName})", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(now.AddMinutes(-5), now.Add(CaValidity));
    }

    /// <summary>新規ノード側で鍵を生成し、CSR(PEM)と秘密鍵(PEM)を返す。秘密鍵はノードの外に出さない。</summary>
    public static (string CsrPem, string PrivateKeyPem) CreateNodeCsr(string nodeName)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={nodeName}", key, HashAlgorithmName.SHA256);
        return (request.CreateSigningRequestPem(), key.ExportPkcs8PrivateKeyPem());
    }

    /// <summary>
    /// CA側でCSRに署名する。CSRのサブジェクト・拡張は信用せず、CA側で決めた内容（ノードID・名前・アドレス）で発行する。
    /// </summary>
    public static X509Certificate2 SignNodeCsr(
        X509Certificate2 ca, string csrPem, Guid nodeId, string nodeName, IEnumerable<string> addresses, DateTimeOffset now)
    {
        var csr = CertificateRequest.LoadSigningRequestPem(csrPem, HashAlgorithmName.SHA256,
            CertificateRequestLoadOptions.Default, RSASignaturePadding.Pkcs1);

        var request = new CertificateRequest(new X500DistinguishedName($"CN={nodeName}"), csr.PublicKey, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([ServerAuth, ClientAuth], false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, true, false));

        var san = new SubjectAlternativeNameBuilder();
        san.AddUri(new Uri(NodeUriScheme + nodeId));
        foreach (var address in addresses.Distinct())
        {
            if (IPAddress.TryParse(address, out var ip)) san.AddIpAddress(ip);
            else san.AddDnsName(address);
        }
        request.CertificateExtensions.Add(san.Build());

        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        var notAfter = now.Add(NodeValidity) < ca.NotAfter ? now.Add(NodeValidity) : new DateTimeOffset(ca.NotAfter);
        return request.Create(ca, now.AddMinutes(-5), notAfter, serial);
    }

    /// <summary>
    /// 証明書がクラスタCAで署名されたノード証明書か検証し、ノードIDを返す。OSの信頼ストアは使わない。
    /// </summary>
    public static Guid? ValidateNodeCertificate(X509Certificate2 certificate, X509Certificate2 ca)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        // 失効機能は設けない（ADR-012）。エアギャップのためオンライン確認もしない
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        if (!chain.Build(certificate)) return null;
        if (!chain.ChainElements[^1].Certificate.RawData.AsSpan().SequenceEqual(ca.RawData)) return null;

        return NodeIdOf(certificate);
    }

    public static Guid? NodeIdOf(X509Certificate2 certificate)
    {
        var san = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        if (san is null) return null;
        // .NETには URI を取り出すAPIが無いため、ASN.1を直接読む（GeneralName [6] uniformResourceIdentifier）
        var reader = new System.Formats.Asn1.AsnReader(san.RawData, System.Formats.Asn1.AsnEncodingRules.DER).ReadSequence();
        var uriTag = new System.Formats.Asn1.Asn1Tag(System.Formats.Asn1.TagClass.ContextSpecific, 6);
        while (reader.HasData)
        {
            var tag = reader.PeekTag();
            if (tag.HasSameClassAndValue(uriTag))
            {
                var uri = reader.ReadCharacterString(System.Formats.Asn1.UniversalTagNumber.IA5String, uriTag);
                if (uri.StartsWith(NodeUriScheme, StringComparison.Ordinal) && Guid.TryParse(uri[NodeUriScheme.Length..], out var id))
                    return id;
            }
            else
            {
                reader.ReadEncodedValue();
            }
        }
        return null;
    }

    /// <summary>参加トークンに埋め込む、CA証明書のSHA-256指紋（16進）。</summary>
    public static string Fingerprint(X509Certificate2 certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.RawData));

    public static X509Certificate2 AsCertificate2(X509Certificate certificate) =>
        certificate as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());

    public static X509Certificate2 WithPrivateKey(string certificatePem, string privateKeyPem)
    {
        using var cert = X509Certificate2.CreateFromPem(certificatePem, privateKeyPem);
        // SslStream（特にLinux）で使うため、エフェメラルキーではなくPKCS#12経由で読み直す
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12), null);
    }
}
