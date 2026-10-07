using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace Cluckwork.Api.IntegrationTests;

// #794 — what the key ring does when its certificate passes NotAfter, and when a
// certificate is reissued on the same key pair. Each protector is a separate ring
// sharing one key directory, so the second one has to decrypt the stored key.
public sealed class DataProtectionCertificateExpiryTests : IDisposable
{
    private readonly DirectoryInfo _keys = Directory.CreateTempSubdirectory("cw794-keys-");
    private readonly List<ServiceProvider> _providers = [];

    [Fact]
    public void An_expired_certificate_still_encrypts_and_decrypts_the_key_ring()
    {
        using var rsa = RSA.Create(2048);
        using var expired = Certificate(rsa, notAfter: DateTimeOffset.UtcNow.AddDays(-1));

        var payload = Protector(expired).Protect("reset-token");

        Assert.Contains("<EncryptedData", File.ReadAllText(Assert.Single(_keys.GetFiles()).FullName), StringComparison.Ordinal);
        Assert.Equal("reset-token", Protector(expired).Unprotect(payload));
    }

    [Fact]
    public void A_certificate_reissued_on_the_same_key_pair_cannot_read_the_old_ring()
    {
        using var rsa = RSA.Create(2048);
        using var original = Certificate(rsa, notAfter: DateTimeOffset.UtcNow.AddYears(1));
        using var reissued = Certificate(rsa, notAfter: DateTimeOffset.UtcNow.AddYears(2));

        var payload = Protector(original).Protect("reset-token");

        Assert.Throws<CryptographicException>(() => Protector(reissued).Unprotect(payload));
    }

    private IDataProtector Protector(X509Certificate2 certificate)
    {
        var services = new ServiceCollection();
        services.AddDataProtection()
            .SetApplicationName("Cluckwork")
            .PersistKeysToFileSystem(_keys)
            .ProtectKeysWithCertificate(certificate);
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        return provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("Cluckwork.IntegrationTests.794");
    }

    private static X509Certificate2 Certificate(RSA key, DateTimeOffset notAfter)
    {
        var request = new CertificateRequest(
            "CN=cluckwork-test-data-protection", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(notAfter.AddYears(-2), notAfter);
    }

    public void Dispose()
    {
        foreach (var provider in _providers)
            provider.Dispose();
        _keys.Delete(recursive: true);
    }
}
