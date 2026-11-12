using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Infrastructure.Modules.Access.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
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

    [Fact]
    public async Task A_key_encrypted_with_the_previous_certificate_still_unprotects_and_new_keys_use_the_current_one()
    {
        var payload = factory.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(Purpose).Protect("authenticator-key");
        await using var rotated = Host(withPreviousCertificate: true);

        Assert.Equal("authenticator-key", rotated.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(Purpose).Unprotect(payload));

        var key = rotated.Services.GetRequiredService<IKeyManager>()
            .CreateNewKey(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(90));
        using var scope = factory.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<AppDbContext>().DataProtectionKeys
            .Select(k => k.Xml!).ToListAsync();
        var xml = Assert.Single(stored, x => x.Contains(key.KeyId.ToString(), StringComparison.Ordinal));
        Assert.Contains(Base64(TestDataProtectionCertificate.CertificatePem), xml, StringComparison.Ordinal);
        Assert.DoesNotContain(Base64(TestDataProtectionCertificate.PreviousCertificatePem), xml, StringComparison.Ordinal);
    }

    // The previous certificate really gone: the read fails, which is what makes two-factor
    // log Auth.SecondFactorKeyUnreadable and fall back to recovery codes.
    [Fact]
    public async Task Without_the_previous_certificate_the_old_key_is_unreadable()
    {
        var payload = factory.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(Purpose).Protect("authenticator-key");
        await using var rotated = Host(withPreviousCertificate: false);

        Assert.Throws<CryptographicException>(() => rotated.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(Purpose).Unprotect(payload));
    }

    // Certificate B, the suite's usual one, is current. A is listed only when asked.
    private WebApplicationFactory<Program> Host(bool withPreviousCertificate) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("DataProtection:CertificatePem", TestDataProtectionCertificate.CertificatePem);
            builder.UseSetting("DataProtection:PrivateKeyPem", TestDataProtectionCertificate.PrivateKeyPem);
            if (!withPreviousCertificate)
                return;
            builder.UseSetting("DataProtection:PreviousCertificates:0:CertificatePem", TestDataProtectionCertificate.PreviousCertificatePem);
            builder.UseSetting("DataProtection:PreviousCertificates:0:PrivateKeyPem", TestDataProtectionCertificate.PreviousPrivateKeyPem);
        });

    // The certificate as the key XML stores it, in its X509Certificate element.
    private static string Base64(string certificatePem) =>
        Convert.ToBase64String(X509Certificate2.CreateFromPem(PemKey.Normalize(certificatePem)).RawData);

    public sealed class CertificateAFactory : CluckworkWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("DataProtection:CertificatePem", TestDataProtectionCertificate.PreviousCertificatePem);
            builder.UseSetting("DataProtection:PrivateKeyPem", TestDataProtectionCertificate.PreviousPrivateKeyPem);
        }
    }
}
