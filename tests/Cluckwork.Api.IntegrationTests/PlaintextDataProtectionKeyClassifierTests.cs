using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using Cluckwork.Api.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.DependencyInjection;

namespace Cluckwork.Api.IntegrationTests;

// #794 — the plaintext guard decides by structure over XML the framework itself wrote, so
// an edited or missing requiresEncryption marker cannot hide a readable master key.
public sealed class PlaintextDataProtectionKeyClassifierTests : IDisposable
{
    private static readonly XNamespace DataProtection = "http://schemas.asp.net/2015/03/dataProtection";
    private readonly List<DirectoryInfo> _directories = [];

    [Fact]
    public void A_framework_plaintext_key_is_flagged()
    {
        var key = Single(WrittenKeys(Plaintext));

        Assert.Equal([Id(key)], Classify(key));
    }

    public static TheoryData<string> MarkerEdits() =>
        ["removed", "value 1", "value True", "unqualified", "other namespace"];

    [Theory]
    [MemberData(nameof(MarkerEdits))]
    public void A_plaintext_key_with_its_marker_edited_is_still_flagged(string edit)
    {
        var key = Single(WrittenKeys(Plaintext));
        var masterKey = key.Descendants("masterKey").Single();
        masterKey.Attributes().Where(a => a.Name.LocalName == "requiresEncryption").Remove();
        switch (edit)
        {
            case "value 1": masterKey.SetAttributeValue(DataProtection + "requiresEncryption", "1"); break;
            case "value True": masterKey.SetAttributeValue(DataProtection + "requiresEncryption", "True"); break;
            case "unqualified": masterKey.SetAttributeValue("requiresEncryption", "true"); break;
            case "other namespace": masterKey.SetAttributeValue(XName.Get("requiresEncryption", "urn:other"), "true"); break;
        }

        Assert.Equal([Id(key)], Classify(key));
    }

    [Fact]
    public void A_certificate_encrypted_key_passes()
    {
        Assert.Empty(Classify(Single(WrittenKeys(CertificateEncrypted))));
    }

    [Fact]
    public void An_encrypted_key_carrying_an_unmarked_plaintext_sibling_is_flagged()
    {
        var encrypted = Single(WrittenKeys(CertificateEncrypted));
        var plaintextMasterKey = Single(WrittenKeys(Plaintext)).Descendants("masterKey").Single();
        plaintextMasterKey.Attributes().Where(a => a.Name.LocalName == "requiresEncryption").Remove();
        encrypted.Descendants(DataProtection + "encryptedSecret").Single().AddAfterSelf(plaintextMasterKey);

        Assert.Equal([Id(encrypted)], Classify(encrypted));
    }

    // NullXmlEncryptor writes an encryptedSecret wrapper whose content is still the clear
    // master key, so the wrapper's name proves nothing.
    [Fact]
    public void A_key_behind_the_null_encryptor_wrapper_is_flagged()
    {
        var key = Single(WrittenKeys(services =>
            services.Configure<KeyManagementOptions>(o => o.XmlEncryptor = new NullXmlEncryptor())));

        Assert.Equal([Id(key)], Classify(key));
    }

    [Fact]
    public void A_revocation_record_passes()
    {
        var records = WrittenKeys(CertificateEncrypted, revokeAll: true);
        var revocation = Assert.Single(records, r => r.Name.LocalName == "revocation");

        Assert.Empty(Classify(revocation));
        Assert.Empty(PlaintextDataProtectionKeyGuard.PlaintextKeyIds(records.Select(r => r.ToString())));
    }

    private static void Plaintext(IServiceCollection services)
    {
    }

    private static void CertificateEncrypted(IServiceCollection services)
    {
        using var rsa = RSA.Create(2048);
        var certificate = new CertificateRequest(
                "CN=cluckwork-test-data-protection", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        services.AddDataProtection().ProtectKeysWithCertificate(certificate);
    }

    private List<XElement> WrittenKeys(Action<IServiceCollection> configure, bool revokeAll = false)
    {
        var directory = Directory.CreateTempSubdirectory("cw794-classifier-");
        _directories.Add(directory);
        var services = new ServiceCollection();
        services.AddDataProtection().SetApplicationName("Cluckwork").PersistKeysToFileSystem(directory);
        configure(services);
        using (var provider = services.BuildServiceProvider())
        {
            provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("p").Protect("x");
            if (revokeAll)
                provider.GetRequiredService<IKeyManager>().RevokeAllKeys(DateTimeOffset.UtcNow, "test");
        }

        return [.. directory.GetFiles().Select(f => XElement.Load(f.FullName))];
    }

    private static XElement Single(List<XElement> records) => Assert.Single(records);

    private static string Id(XElement key) => (string)key.Attribute("id")!;

    private static IReadOnlyList<string> Classify(XElement record) =>
        PlaintextDataProtectionKeyGuard.PlaintextKeyIds([record.ToString()]);

    public void Dispose()
    {
        foreach (var directory in _directories)
            directory.Delete(recursive: true);
    }
}
