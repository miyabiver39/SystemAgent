using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SystemAgent.Infrastructure.Cluster;

namespace SystemAgent.Core.Tests;

public class PkiTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void SignedNodeCertificate_ValidatesAgainstClusterCa_AndCarriesNodeId()
    {
        using var ca = Pki.CreateCa("test", Now);
        var nodeId = Guid.NewGuid();
        var (csr, key) = Pki.CreateNodeCsr("node-a");

        using var cert = Pki.SignNodeCsr(ca, csr, nodeId, "node-a", ["10.0.0.11", "node-a.local"], Now);

        Assert.Equal(nodeId, Pki.ValidateNodeCertificate(cert, ca, NodeCertificateUsage.Server));
        Assert.Equal(nodeId, Pki.ValidateNodeCertificate(cert, ca, NodeCertificateUsage.Client));
        Assert.Equal("CN=node-a", cert.Subject);
        Assert.True(cert.NotAfter <= ca.NotAfter);

        using var withKey = Pki.WithPrivateKey(cert.ExportCertificatePem(), key);
        Assert.True(withKey.HasPrivateKey);
    }

    [Fact]
    public void CertificateFromAnotherCa_IsRejected()
    {
        using var ca = Pki.CreateCa("ours", Now);
        using var otherCa = Pki.CreateCa("theirs", Now);
        var (csr, _) = Pki.CreateNodeCsr("intruder");
        using var cert = Pki.SignNodeCsr(otherCa, csr, Guid.NewGuid(), "intruder", ["10.0.0.99"], Now);

        Assert.Null(Pki.ValidateNodeCertificate(cert, ca, NodeCertificateUsage.Client));
    }

    [Fact]
    public void SelfSignedCertificate_IsRejected()
    {
        using var ca = Pki.CreateCa("ours", Now);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var self = new CertificateRequest("CN=fake", key, HashAlgorithmName.SHA256).CreateSelfSigned(Now, Now.AddDays(1));

        Assert.Null(Pki.ValidateNodeCertificate(self, ca, NodeCertificateUsage.Client));
    }

    [Theory]
    [InlineData(NodeCertificateUsage.Server)]
    [InlineData(NodeCertificateUsage.Client)]
    public void CertificateWithoutRequiredUsage_IsRejected(NodeCertificateUsage usage)
    {
        // 同じCAで署名されていても、用途（EKU）が違う・無い証明書は受け入れない
        using var ca = Pki.CreateCa("test", Now);
        var nodeId = Guid.NewGuid();
        var other = usage == NodeCertificateUsage.Server ? "1.3.6.1.5.5.7.3.2" : "1.3.6.1.5.5.7.3.1";
        using var wrongUsage = SignWithUsages(ca, nodeId, [other]);
        using var noUsage = SignWithUsages(ca, nodeId, null);

        Assert.Null(Pki.ValidateNodeCertificate(wrongUsage, ca, usage));
        Assert.Null(Pki.ValidateNodeCertificate(noUsage, ca, usage));
    }

    [Fact]
    public void Validate_AcceptsBaseX509CertificateFromTlsCallback()
    {
        using var ca = Pki.CreateCa("test", Now);
        var nodeId = Guid.NewGuid();
        var (csr, _) = Pki.CreateNodeCsr("node-a");
        using var cert = Pki.SignNodeCsr(ca, csr, nodeId, "node-a", ["10.0.0.11"], Now);
#pragma warning disable SYSLIB0026, SYSLIB0057 // TLSのコールバックで渡される基底クラスの証明書を再現する
        using var baseCertificate = new X509Certificate(cert.RawData);
#pragma warning restore SYSLIB0026, SYSLIB0057

        Assert.Equal(nodeId, Pki.ValidateNodeCertificate(baseCertificate, ca, NodeCertificateUsage.Server));
    }

    private static X509Certificate2 SignWithUsages(X509Certificate2 ca, Guid nodeId, string[]? usages)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=node-x", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        if (usages is not null)
        {
            var oids = new OidCollection();
            foreach (var usage in usages) oids.Add(new Oid(usage));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(oids, false));
        }
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, true, false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddUri(new Uri(Pki.NodeUriScheme + nodeId));
        request.CertificateExtensions.Add(san.Build());
        return request.Create(ca, Now.AddMinutes(-5), Now.AddDays(1), RandomNumberGenerator.GetBytes(16));
    }

    [Fact]
    public void Csr_SubjectIsNotTrusted()
    {
        // CSRに何を書かれても、CA側で決めたノード名・IDで発行される
        using var ca = Pki.CreateCa("test", Now);
        var (csr, _) = Pki.CreateNodeCsr("admin-pretending");
        using var cert = Pki.SignNodeCsr(ca, csr, Guid.NewGuid(), "node-b", ["10.0.0.12"], Now);

        Assert.Equal("CN=node-b", cert.Subject);
    }

    [Fact]
    public void TamperedCsr_IsRejected()
    {
        using var ca = Pki.CreateCa("test", Now);
        var (csr, _) = Pki.CreateNodeCsr("node-a");
        var body = csr.Split('\n').Where(l => !l.StartsWith("-----")).Aggregate(string.Concat);
        var bytes = Convert.FromBase64String(body);
        bytes[^10] ^= 0xFF;
        var tampered = "-----BEGIN CERTIFICATE REQUEST-----\n" + Convert.ToBase64String(bytes) + "\n-----END CERTIFICATE REQUEST-----";

        Assert.ThrowsAny<CryptographicException>(() => Pki.SignNodeCsr(ca, tampered, Guid.NewGuid(), "node-a", [], Now));
    }

    [Fact]
    public void Fingerprint_IsSha256Hex()
    {
        using var ca = Pki.CreateCa("test", Now);
        Assert.Matches("^[0-9A-F]{64}$", Pki.Fingerprint(ca));
    }
}
