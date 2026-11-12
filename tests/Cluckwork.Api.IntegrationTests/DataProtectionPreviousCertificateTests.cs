using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Infrastructure.Modules.Access.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cluckwork.Api.IntegrationTests;

// #1338 — after a planned certificate change, keys the previous certificate encrypted stay
// readable while it is listed, and only the current certificate encrypts. The fixture's
// host runs on certificate A, already expired, so its startup stores A's key in the table.
public sealed class DataProtectionPreviousCertificateTests(
    DataProtectionPreviousCertificateTests.CertificateAFactory factory)
    : IClassFixture<DataProtectionPreviousCertificateTests.CertificateAFactory>
{
    private const string Purpose = "Cluckwork.IntegrationTests.1338";

    private static readonly (string Certificate, string Key) A =
        (TestDataProtectionCertificate.PreviousCertificatePem, TestDataProtectionCertificate.PreviousPrivateKeyPem);

    private static readonly (string Certificate, string Key) B =
        (TestDataProtectionCertificate.CertificatePem, TestDataProtectionCertificate.PrivateKeyPem);

    [Fact]
    public async Task A_key_encrypted_with_the_previous_certificate_still_unprotects_and_new_keys_use_the_current_one()
    {
        using (var a = Certificate(A.Certificate))
            Assert.True(a.NotAfter < DateTime.Now, "certificate A must already be expired");
        var payload = Protect(factory);
        await using var rotated = Host(B, (0, A));

        Assert.Equal("authenticator-key", Unprotect(rotated, payload));
        await AssertNewKeyIsEncryptedWithAsync(rotated, B, notWith: A);
    }

    // Two changes in a row, with a gap in the numbering: A, then B, then C.
    [Fact]
    public async Task Every_listed_previous_certificate_still_unprotects()
    {
        var payloadA = Protect(factory);
        Guid keyB;
        await using (var issuing = Host(B, (0, A)))
            keyB = issuing.Services.GetRequiredService<IKeyManager>()
                .CreateNewKey(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(90)).KeyId;
        // A fresh host loads the ring with B's key as the newest, so its payload is under B.
        await using var withB = Host(B, (0, A));
        var payloadB = Protect(withB);
        Assert.Equal(keyB, new Guid(WebEncoders.Base64UrlDecode(payloadB).AsSpan(4, 16)));
        var c = TestDataProtectionCertificate.Generate(RSA.Create(2048));
        await using var withC = Host(c, (0, A), (2, B));

        Assert.Equal("authenticator-key", Unprotect(withC, payloadA));
        Assert.Equal("authenticator-key", Unprotect(withC, payloadB));
        await AssertNewKeyIsEncryptedWithAsync(withC, c, notWith: B);
    }

    // A renewal on A's key pair is a different certificate, so it boots with A listed and
    // still needs A to read A's keys.
    [Fact]
    public async Task A_renewal_on_the_same_key_pair_boots_with_the_original_listed()
    {
        var payload = Protect(factory);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(PemKey.Normalize(A.Key));
        using var renewal = new CertificateRequest(
                "CN=cluckwork-test-data-protection", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        await using var host = Host((renewal.ExportCertificatePem().ReplaceLineEndings("\\n"), A.Key), (0, A));

        Assert.Equal("authenticator-key", Unprotect(host, payload));
    }

    // The previous certificate really gone: the read fails, which is what makes two-factor
    // log Auth.SecondFactorKeyUnreadable and fall back to recovery codes.
    [Fact]
    public async Task Without_the_previous_certificate_the_old_key_is_unreadable()
    {
        var payload = Protect(factory);
        await using var rotated = Host(B);

        Assert.Throws<CryptographicException>(() => Unprotect(rotated, payload));
    }

    private static string Protect(WebApplicationFactory<Program> host) =>
        host.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Protect("authenticator-key");

    private static string Unprotect(WebApplicationFactory<Program> host, string payload) =>
        host.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Unprotect(payload);

    private WebApplicationFactory<Program> Host(
        (string Certificate, string Key) current, params (int Index, (string Certificate, string Key) Pair)[] previous) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("DataProtection:CertificatePem", current.Certificate);
            builder.UseSetting("DataProtection:PrivateKeyPem", current.Key);
            foreach (var (index, pair) in previous)
            {
                builder.UseSetting($"DataProtection:PreviousCertificates:{index}:CertificatePem", pair.Certificate);
                builder.UseSetting($"DataProtection:PreviousCertificates:{index}:PrivateKeyPem", pair.Key);
            }
        });

    // The key XML stores the encrypting certificate, base64, in its X509Certificate element.
    private async Task AssertNewKeyIsEncryptedWithAsync(
        WebApplicationFactory<Program> host, (string Certificate, string Key) expected, (string Certificate, string Key) notWith)
    {
        var key = host.Services.GetRequiredService<IKeyManager>()
            .CreateNewKey(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(90));
        using var scope = factory.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<AppDbContext>().DataProtectionKeys
            .Select(k => k.Xml!).ToListAsync();
        var xml = Assert.Single(stored, x => x.Contains(key.KeyId.ToString(), StringComparison.Ordinal));
        using var expectedCertificate = Certificate(expected.Certificate);
        using var otherCertificate = Certificate(notWith.Certificate);
        Assert.Contains(Convert.ToBase64String(expectedCertificate.RawData), xml, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(otherCertificate.RawData), xml, StringComparison.Ordinal);
    }

    private static X509Certificate2 Certificate(string pem) => X509Certificate2.CreateFromPem(PemKey.Normalize(pem));

    public sealed class CertificateAFactory : CluckworkWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("DataProtection:CertificatePem", A.Certificate);
            builder.UseSetting("DataProtection:PrivateKeyPem", A.Key);
        }
    }
}
