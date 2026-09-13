namespace Cluckwork.Api.IntegrationTests.Infrastructure;

using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

// #344 — a server certificate for the real-TLS Kestrel listener, generated at
// test-process startup and never persisted. Same rule as TestJwtKeys: no key
// material under source control for a secret scanner to find, and nothing that
// depends on `dotnet dev-certs` having been run on the machine (CI has not).
//
// The SAN list is what lets one listener stand in for several hosts. The client
// dials 127.0.0.1 whatever hostname the request URI names (see
// TlsClient.ConnectCallback), so the URI authority — and therefore SNI, the
// Host header and Request.Host — is free to be `cluckwork.test`, a host HSTS's
// default ExcludedHosts does NOT skip. That is what makes both directions of
// the header observable over one connection: the loopback names are excluded
// and must stay bare, `cluckwork.test` is not and must carry the header.
internal static class TestTlsCertificate
{
    // The host AllowedHosts pins in CluckworkWebApplicationFactory. Not
    // loopback, so not in HstsOptions.ExcludedHosts' defaults.
    public const string PublicHost = "cluckwork.test";

    private static readonly Lazy<X509Certificate2> Cert = new(Generate);

    public static X509Certificate2 Certificate => Cert.Value;

    // Identity is pinned by exact hash rather than by building a trust chain:
    // the client must accept THIS certificate and no other, so a test cannot
    // pass against some other server that happened to answer on the port.
    public static string Sha256Thumbprint => Certificate.GetCertHashString(HashAlgorithmName.SHA256);

    private static X509Certificate2 Generate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={PublicHost}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(PublicHost);
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true)); // serverAuth

        using var selfSigned = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(2));

        // Round-trip through PKCS#12 so the private key is attached in the form
        // Kestrel's UseHttps requires on every platform, not just this one.
        return X509CertificateLoader.LoadPkcs12(selfSigned.Export(X509ContentType.Pfx), password: null);
    }
}
