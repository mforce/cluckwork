using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Xml.Linq;
using Cluckwork.Api.Hosting;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Infrastructure.Modules.Access.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Cluckwork.Api.IntegrationTests;

// #1420 — `rewrap-key-ring` re-encrypts every stored key under the current certificate, so a
// previous certificate can leave the configuration. Each case runs the built API as the
// operator does. The rows come from the framework itself, encrypted to certificate A.
public sealed class DataProtectionKeyRewrapTests(DataProtectionKeyRewrapTests.Database database)
    : IClassFixture<DataProtectionKeyRewrapTests.Database>, IAsyncLifetime
{
    private const string Purpose = "Cluckwork.IntegrationTests.1420";
    private const string NotTheEnvelope = "is not in the envelope the framework writes";
    private const string OtherDecryptor = "names a decryptor other than EncryptedXmlDecryptor";
    private const string Plaintext = "holds an unencrypted master key";
    private static readonly XNamespace Dp = "http://schemas.asp.net/2015/03/dataProtection";
    private static readonly XNamespace Dsig = "http://www.w3.org/2000/09/xmldsig#";
    private static readonly XNamespace Xenc = "http://www.w3.org/2001/04/xmlenc#";
    private static readonly string ApiDllPath = typeof(Program).Assembly.Location;

    private static readonly Pair A =
        new(TestDataProtectionCertificate.PreviousCertificatePem, TestDataProtectionCertificate.PreviousPrivateKeyPem);

    private static readonly Pair B =
        new(TestDataProtectionCertificate.CertificatePem, TestDataProtectionCertificate.PrivateKeyPem);

    private static readonly Lazy<Pair> LazyC = new(() =>
    {
        var (certificate, key) = TestDataProtectionCertificate.Generate(RSA.Create(2048));
        return new(certificate, key);
    });

    private static Pair C => LazyC.Value;

    public Task InitializeAsync() => ExecuteAsync("DELETE FROM \"DataProtectionKeys\"");

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Every_stored_key_is_rewrapped_to_the_current_certificate_and_stays_readable()
    {
        var ring = new MemoryRepository();
        string payload;
        Guid revoked;
        using (var host = Host(ring, A))
        {
            payload = host.GetDataProtector(Purpose).Protect("authenticator-key");
            var keys = host.GetRequiredService<IKeyManager>();
            var now = DateTimeOffset.UtcNow;
            keys.CreateNewKey(now.AddDays(-100), now.AddDays(-10));
            revoked = keys.CreateNewKey(now.AddDays(-50), now.AddDays(-5)).KeyId;
            keys.RevokeKey(revoked, "1420");
            AsWrittenByVersion8(ring.Key(keys.CreateNewKey(now.AddDays(-30), now.AddDays(60)).KeyId));
            AddSecondSecret(ring.Key(keys.CreateNewKey(now.AddDays(-20), now.AddDays(70)).KeyId), A, first: true);
            AddSecondSecret(ring.Key(keys.CreateNewKey(now.AddDays(-15), now.AddDays(75)).KeyId), B, first: true);
            AddSecondSecret(ring.Key(keys.CreateNewKey(now.AddDays(-12), now.AddDays(78)).KeyId), B, first: false);
        }
        await InsertAsync(ring.Rows);
        var before = await RowsAsync();
        Assert.Equal(8, before.Count);
        var clearBefore = ClearSecrets(before, A, B);
        var masterKeys = clearBefore.SelectMany(c => c.DescendantsAndSelf().Where(e => e.Name.LocalName == "masterKey"))
            .Select(e => e.Value).ToList();
        Assert.Equal(7, masterKeys.Count);

        var (exitCode, stdout, stderr) = await RewrapAsync(B, [A]);

        Assert.True(exitCode == 0, $"exit {exitCode}\nstdout={stdout}\nstderr={stderr}");
        var after = await RowsAsync();
        Assert.Equal(before.Select(r => (r.Id, r.FriendlyName)), after.Select(r => (r.Id, r.FriendlyName)));
        foreach (var (old, rewrapped) in before.Zip(after))
        {
            if (XElement.Parse(old.Xml!).Name == "revocation")
            {
                Assert.Equal(old.Xml, rewrapped.Xml);
                continue;
            }
            Assert.Equal(OutsideCiphertext(old.Xml!), OutsideCiphertext(rewrapped.Xml!));
            Assert.All(EmbeddedFingerprints(rewrapped.Xml!), fingerprint => Assert.Equal(B.Sha256, fingerprint));
            var fingerprintsBefore = string.Join(",", EmbeddedFingerprints(old.Xml!).Distinct());
            Assert.Contains($"{KeyId(old.Xml!)}\t{fingerprintsBefore}\t{B.Sha256}", stdout, StringComparison.Ordinal);
        }
        var clearAfter = ClearSecrets(after, B);
        Assert.True(
            clearBefore.Count == clearAfter.Count && clearBefore.Zip(clearAfter).All(p => XNode.DeepEquals(p.First, p.Second)),
            "every decrypted secret must be unchanged by the re-wrap");
        Assert.Empty(PlaintextDataProtectionKeyGuard.PlaintextKeyIds(after.Select(r => r.Xml)));
        Assert.Contains($"current certificate SHA-256 {B.Sha256}", stdout, StringComparison.Ordinal);
        Assert.Contains("rewrapped 7, already current 0, revocations 1", stdout, StringComparison.Ordinal);
        var logLine = Assert.Single(stdout.Split('\n'), l => l.Contains("rewrap-key-ring rewrote", StringComparison.Ordinal));
        Assert.Contains(B.Sha256, logLine, StringComparison.Ordinal);
        Assert.Contains(A.Sha256, logLine, StringComparison.Ordinal);
        AssertNoKeyMaterial(stdout, stderr, masterKeys);

        using (var reader = Host(new MemoryRepository(after), B))
        {
            var keys = reader.GetRequiredService<IKeyManager>().GetAllKeys();
            Assert.Equal(
                ring.Rows.Where(r => r.Xml.Name == "key").Select(r => KeyId(r.Xml)).Order(),
                keys.Select(k => k.KeyId.ToString()).Order());
            Assert.All(keys, key => Assert.NotNull(key.Descriptor));
            Assert.True(keys.Single(k => k.KeyId == revoked).IsRevoked, "the revoked key must stay revoked");
            Assert.Equal("authenticator-key", reader.GetDataProtector(Purpose).Unprotect(payload));
        }

        var rerun = await RewrapAsync(B, [A]);
        Assert.True(rerun.ExitCode == 0, $"rerun exit {rerun.ExitCode}\nstderr={rerun.Stderr}");
        Assert.Contains("rewrapped 0, already current 7, revocations 1", rerun.Stdout, StringComparison.Ordinal);
        AssertNoKeyMaterial(rerun.Stdout, rerun.Stderr, masterKeys);
        Assert.Equal(after, await RowsAsync());

        var withoutPrevious = await RewrapAsync(B, []);
        Assert.True(withoutPrevious.ExitCode == 0, $"check exit {withoutPrevious.ExitCode}\nstderr={withoutPrevious.Stderr}");
        AssertNoKeyMaterial(withoutPrevious.Stdout, withoutPrevious.Stderr, masterKeys);
        Assert.Equal(after, await RowsAsync());
    }

    // A save per row would leave the first row rewritten when the second update fails.
    [Fact]
    public async Task A_failed_write_changes_no_row()
    {
        await InsertAsync(KeysUnder(A, 2));
        var before = await RowsAsync();
        await ExecuteAsync(
            "CREATE FUNCTION fail_1420() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'forced failure 1420'; END $$; "
            + "CREATE TRIGGER fail_1420 BEFORE UPDATE ON \"DataProtectionKeys\" FOR EACH ROW "
            + $"WHEN (NEW.\"Id\" = {before[1].Id}) EXECUTE FUNCTION fail_1420();");
        (int ExitCode, string Stdout, string Stderr) result;
        try
        {
            result = await RewrapAsync(B, [A]);
        }
        finally
        {
            await ExecuteAsync("DROP TRIGGER fail_1420 ON \"DataProtectionKeys\"; DROP FUNCTION fail_1420();");
        }

        Assert.True(result.ExitCode != 0, $"stdout={result.Stdout}");
        Assert.False(
            result.Stdout.Contains($"\t{B.Sha256}", StringComparison.Ordinal),
            $"a failed write must print no row line with an after fingerprint. stdout={result.Stdout}");
        Assert.Equal(before, await RowsAsync());
    }

    [Fact]
    public async Task A_row_no_configured_certificate_decrypts_refuses_the_whole_run()
    {
        await InsertAsync([.. KeysUnder(A, 1), .. KeysUnder(B, 1), .. KeysUnder(C, 1)]);
        var before = await RowsAsync();

        var (exitCode, stdout, stderr) = await RewrapAsync(B, [A]);

        Assert.True(exitCode == 1, $"exit {exitCode}\nstdout={stdout}\nstderr={stderr}");
        Assert.Contains(C.Sha256, stderr, StringComparison.Ordinal);
        Assert.Equal(before, await RowsAsync());
    }

    // The embedded certificate says B, but the key inside was encrypted to A. Only a decrypt
    // tells the two apart.
    [Fact]
    public async Task A_row_that_names_the_current_certificate_but_does_not_decrypt_refuses_the_run()
    {
        var rows = KeysUnder(A, 2);
        rows[1].Xml.Descendants(Dsig + "X509Certificate").Single().Value = Convert.ToBase64String(B.Load().RawData);
        await InsertAsync(rows);
        var before = await RowsAsync();

        var (exitCode, stdout, stderr) = await RewrapAsync(B, [A]);

        Assert.True(exitCode == 1, $"exit {exitCode}\nstdout={stdout}\nstderr={stderr}");
        Assert.Contains($"row {before[1].Id}", stderr, StringComparison.Ordinal);
        Assert.Equal(before, await RowsAsync());
    }

    // The refusal text tells which check fired: several shapes would also fail later, at the decrypt.
    [Theory]
    [InlineData("unknown decryptor type", OtherDecryptor)]
    [InlineData("decryptor in another assembly", OtherDecryptor)]
    [InlineData("key reached through a RetrievalMethod", NotTheEnvelope)]
    [InlineData("ciphertext by reference", NotTheEnvelope)]
    [InlineData("plaintext key", Plaintext)]
    [InlineData("plaintext key beside a current secret", Plaintext)]
    [InlineData("two recipients", NotTheEnvelope)]
    [InlineData("key without encryptedSecret", "holds no encryptedSecret")]
    [InlineData("unknown root", "is neither a key nor a revocation")]
    [InlineData("empty Xml", "has no XML")]
    public async Task A_row_the_verb_cannot_classify_refuses_the_run(string shape, string refusal)
    {
        var rows = KeysUnder(A, 2);
        var odd = rows[1].Xml;
        string? xml = shape switch
        {
            "unknown decryptor type" => WithDecryptorType(
                odd, "Microsoft.AspNetCore.DataProtection.XmlEncryption.DpapiNGXmlDecryptor, Microsoft.AspNetCore.DataProtection"),
            "decryptor in another assembly" => WithDecryptorType(
                odd, "Microsoft.AspNetCore.DataProtection.XmlEncryption.EncryptedXmlDecryptor, Missing.Assembly"),
            "key reached through a RetrievalMethod" => WithRetrievalMethod(odd),
            "ciphertext by reference" => WithCipherReference(odd),
            "plaintext key" =>
                "<key id=\"00000000-0000-0000-0000-000000001420\" version=\"1\"><descriptor><descriptor>"
                + "<masterKey p4:requiresEncryption=\"true\" xmlns:p4=\"http://schemas.asp.net/2015/03/dataProtection\">"
                + "<value>not-a-real-key</value></masterKey></descriptor></descriptor></key>",
            "plaintext key beside a current secret" => WithClearMasterKey(KeysUnder(B, 1)[0].Xml),
            "two recipients" => WithSecondRecipient(odd),
            "key without encryptedSecret" => WithoutSecrets(odd),
            "unknown root" => "<repository version=\"1\" />",
            "empty Xml" => "",
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
        await InsertAsync([rows[0]]);
        await InsertRawAsync("odd", xml);
        var before = await RowsAsync();

        var (exitCode, stdout, stderr) = await RewrapAsync(B, [A]);

        Assert.True(exitCode == 1, $"exit {exitCode}\nstdout={stdout}\nstderr={stderr}");
        Assert.Contains($"row {before[1].Id}", stderr, StringComparison.Ordinal);
        Assert.Contains(refusal, stderr, StringComparison.Ordinal);
        Assert.Equal(before, await RowsAsync());
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Testing")]
    public async Task No_current_certificate_refuses_in_every_environment(string environment)
    {
        await InsertAsync(KeysUnder(A, 1));
        var before = await RowsAsync();

        var (exitCode, stdout, stderr) = await RewrapAsync(null, [A], environment);

        Assert.True(exitCode == 1, $"exit {exitCode}\nstdout={stdout}\nstderr={stderr}");
        Assert.Equal(before, await RowsAsync());
    }

    // The same boot refusal the server applies: a previous entry with no private key.
    [Fact]
    public async Task A_previous_entry_the_server_refuses_refuses_the_run()
    {
        await InsertAsync(KeysUnder(A, 1));
        var before = await RowsAsync();

        var (exitCode, stdout, stderr) = await RewrapAsync(B, [A, C with { PrivateKeyPem = "" }]);

        Assert.True(exitCode == 1, $"exit {exitCode}\nstdout={stdout}\nstderr={stderr}");
        Assert.Contains("DataProtection:PreviousCertificates:1", stderr, StringComparison.Ordinal);
        Assert.Equal(before, await RowsAsync());
    }

    // EncryptedXml falls back to the user's certificate store. A recipient that is in the store
    // but not in the configuration must still refuse, however the row names it. The store
    // lives under a scratch HOME; the refusal text tells which check fired.
    [Theory]
    [InlineData("unlisted recipient")]
    [InlineData("issuer serial beside a decoy certificate")]
    [InlineData("X509Data outside the EncryptedKey")]
    [InlineData("RetrievalMethod ahead of a decoy EncryptedKey")]
    [InlineData("nested EncryptedData keyed from the store")]
    public async Task A_recipient_only_the_certificate_store_holds_refuses_the_run(string shape)
    {
        var (rows, inStore, refusal) = shape switch
        {
            "unlisted recipient" => (KeysUnder(C, 1), C, $"{C.Sha256}, which is not configured"),
            "issuer serial beside a decoy certificate" =>
                (Reshaped(KeysUnder(A, 1), IssuerSerialBesideDecoy), A, NotTheEnvelope),
            "X509Data outside the EncryptedKey" =>
                (Reshaped(KeysUnder(A, 1), X509DataOutsideEncryptedKey), A, NotTheEnvelope),
            "RetrievalMethod ahead of a decoy EncryptedKey" =>
                (Reshaped(KeysUnder(A, 1), RetrievalMethodAheadOfDecoy), A, NotTheEnvelope),
            "nested EncryptedData keyed from the store" =>
                (Reshaped(KeysUnder(B, 1), NestedEncryptedDataKeyedByC), C, NotTheEnvelope),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
        await InsertAsync(rows);
        var before = await RowsAsync();
        var home = Directory.CreateTempSubdirectory("cluckwork-1420-home-");
        try
        {
            var store = Directory.CreateDirectory(
                Path.Combine(home.FullName, ".dotnet", "corefx", "cryptography", "x509stores", "my"));
            using (var certificate = inStore.Load())
                await File.WriteAllBytesAsync(
                    Path.Combine(store.FullName, $"{certificate.Thumbprint}.pfx"), certificate.Export(X509ContentType.Pkcs12));

            var (exitCode, stdout, stderr) = await RewrapAsync(
                B, [], configure: psi => psi.Environment["HOME"] = home.FullName);

            Assert.True(exitCode == 1, $"exit {exitCode}\nstdout={stdout}\nstderr={stderr}");
            Assert.Contains(refusal, stderr, StringComparison.Ordinal);
            Assert.Equal(before, await RowsAsync());
        }
        finally
        {
            home.Delete(recursive: true);
        }
    }

    // Fixed messages only: a failure must never print the output that holds the key.
    private static void AssertNoKeyMaterial(string stdout, string stderr, IReadOnlyList<string> masterKeys)
    {
        foreach (var output in new[] { stdout, stderr })
        {
            Assert.False(output.Contains("masterKey", StringComparison.Ordinal), "the output names a masterKey element");
            Assert.False(
                masterKeys.Any(k => output.Contains(k, StringComparison.Ordinal)), "the output carries a master key value");
        }
    }

    // Every encrypted element of every key row, decrypted in document order.
    private static List<XElement> ClearSecrets(IEnumerable<Row> rows, params Pair[] certificates)
    {
        using var services = new ServiceCollection().AddDataProtection()
            .UnprotectKeysWithAnyCertificate([.. certificates.Select(c => c.Load())]).Services.BuildServiceProvider();
        var decryptor = new EncryptedXmlDecryptor(services);
        return
        [
            .. rows.Select(r => XElement.Parse(r.Xml!)).Where(x => x.Name == "key")
                .SelectMany(x => x.Descendants(Dp + "encryptedSecret"))
                .Select(secret => decryptor.Decrypt(secret.Elements().Single())),
        ];
    }

    private static List<(string FriendlyName, XElement Xml)> Reshaped(
        List<(string FriendlyName, XElement Xml)> rows, Func<XElement, XElement> reshape) =>
        [.. rows.Select(r => (r.FriendlyName, reshape(r.Xml)))];

    // The X509Data names its recipient by issuer and serial only, which only the certificate
    // store resolves. A configured certificate sits beside it as a decoy.
    private static XElement IssuerSerialBesideDecoy(XElement key)
    {
        var copy = new XElement(key);
        var embedded = copy.Descendants(Dsig + "X509Certificate").Single();
        var x509Data = embedded.Parent!;
        x509Data.Add(IssuerSerial(A));
        embedded.Remove();
        using (var b = B.Load())
            x509Data.Parent!.Add(new XElement("wrapper", new XElement(Dsig + "X509Certificate", Convert.ToBase64String(b.RawData))));
        return copy;
    }

    // The configured certificate sits outside the EncryptedKey, which then names no recipient.
    private static XElement X509DataOutsideEncryptedKey(XElement key)
    {
        var copy = new XElement(key);
        var x509Data = copy.Descendants(Dsig + "X509Data").Single();
        x509Data.Remove();
        using (var b = B.Load())
            x509Data.Element(Dsig + "X509Certificate")!.Value = Convert.ToBase64String(b.RawData);
        copy.Descendants(Xenc + "EncryptedData").Single().Add(x509Data);
        return copy;
    }

    // The EncryptedKey moved out of KeyInfo, which reaches it by a local reference instead.
    private static string WithRetrievalMethod(XElement key)
    {
        var copy = new XElement(key);
        var data = copy.Descendants(Xenc + "EncryptedData").Single();
        var encryptedKey = data.Descendants(Xenc + "EncryptedKey").Single();
        var keyInfo = encryptedKey.Parent!;
        encryptedKey.Remove();
        encryptedKey.SetAttributeValue("Id", "relocated-key");
        keyInfo.Add(new XElement(Dsig + "RetrievalMethod",
            new XAttribute("URI", "#relocated-key"), new XAttribute("Type", "http://www.w3.org/2001/04/xmlenc#EncryptedKey")));
        data.Add(new XElement(Xenc + "EncryptionProperties", new XElement(Xenc + "EncryptionProperty", encryptedKey)));
        return copy.ToString(SaveOptions.DisableFormatting);
    }

    private static List<(string FriendlyName, XElement Xml)> KeysUnder(Pair certificate, int count)
    {
        var ring = new MemoryRepository();
        using (var host = Host(ring, certificate))
        {
            var keys = host.GetRequiredService<IKeyManager>();
            for (var i = 0; i < count; i++)
                keys.CreateNewKey(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(90));
        }
        return ring.Rows;
    }

    private static ServiceProvider Host(IXmlRepository repository, Pair current)
    {
        var services = new ServiceCollection();
        services.AddDataProtection().SetApplicationName("Cluckwork").ProtectKeysWithCertificate(current.Load());
        services.Configure<KeyManagementOptions>(o => o.XmlRepository = repository);
        return services.BuildServiceProvider();
    }

    // A ring written by an older framework names its types with that framework's version.
    private static void AsWrittenByVersion8(XElement key)
    {
        var version = $"Version={typeof(EncryptedXmlDecryptor).Assembly.GetName().Version}";
        var typed = key.DescendantsAndSelf().Attributes()
            .Where(a => a.Name.LocalName is "decryptorType" or "deserializerType").ToList();
        Assert.Equal(2, typed.Count);
        Assert.All(typed, a => Assert.Contains(version, a.Value, StringComparison.Ordinal));
        foreach (var attribute in typed)
            attribute.Value = attribute.Value.Replace(version, "Version=8.0.0.0", StringComparison.Ordinal);
    }

    // A second encrypted element before or after the master key's: the framework decrypts both.
    private static void AddSecondSecret(XElement key, Pair recipient, bool first)
    {
        var secret = key.Descendants(Dp + "encryptedSecret").Single();
        using var certificate = recipient.Load();
        var encrypted = new CertificateXmlEncryptor(certificate, NullLoggerFactory.Instance)
            .Encrypt(new XElement("note", "not-key-material")).EncryptedElement;
        var second = new XElement(secret.Name, secret.Attributes(), encrypted);
        if (first)
            secret.AddBeforeSelf(second);
        else
            secret.AddAfterSelf(second);
    }

    private static string WithDecryptorType(XElement key, string decryptorType)
    {
        var copy = new XElement(key);
        copy.Descendants(Dp + "encryptedSecret").Single().SetAttributeValue("decryptorType", decryptorType);
        return copy.ToString(SaveOptions.DisableFormatting);
    }

    private static string WithClearMasterKey(XElement key)
    {
        var copy = new XElement(key);
        copy.Element("descriptor")!.Element("descriptor")!.Add(
            new XElement("masterKey", new XElement("value", "not-a-real-key")));
        return copy.ToString(SaveOptions.DisableFormatting);
    }

    // A second EncryptedKey could name a recipient the configuration never sees.
    private static string WithSecondRecipient(XElement key)
    {
        var copy = new XElement(key);
        var encryptedKey = copy.Descendants(Xenc + "EncryptedKey").Single();
        encryptedKey.AddAfterSelf(new XElement(encryptedKey));
        return copy.ToString(SaveOptions.DisableFormatting);
    }

    // The outer ciphertext named by a URI instead of carried inline.
    private static string WithCipherReference(XElement key)
    {
        var copy = new XElement(key);
        copy.Descendants(Xenc + "EncryptedData").Single().Element(Xenc + "CipherData")!
            .ReplaceNodes(new XElement(Xenc + "CipherReference", new XAttribute("URI", "#ciphertext-1420")));
        return copy.ToString(SaveOptions.DisableFormatting);
    }

    // The outer KeyInfo holds a RetrievalMethod ahead of a decoy EncryptedKey that names B. The
    // reference reaches the real wrapped key, renamed so no second EncryptedKey exists, which
    // names A by issuer and serial only.
    private static XElement RetrievalMethodAheadOfDecoy(XElement key)
    {
        var copy = new XElement(key);
        var data = copy.Descendants(Xenc + "EncryptedData").Single();
        var keyInfo = data.Element(Dsig + "KeyInfo")!;
        var original = keyInfo.Element(Xenc + "EncryptedKey")!;
        var decoy = new XElement(original);
        using (var b = B.Load())
            decoy.Descendants(Dsig + "X509Certificate").Single().Value = Convert.ToBase64String(b.RawData);
        var carried = new XElement(Xenc + "CarriedKey", original.Attributes(), original.Elements());
        carried.SetAttributeValue("Id", "carried-1420");
        var x509Data = carried.Descendants(Dsig + "X509Data").Single();
        x509Data.RemoveNodes();
        x509Data.Add(IssuerSerial(A));
        original.Remove();
        keyInfo.Add(
            new XElement(Dsig + "RetrievalMethod",
                new XAttribute("URI", "#carried-1420"), new XAttribute("Type", EncryptedXml.XmlEncEncryptedKeyUrl)),
            decoy);
        data.Element(Xenc + "EncryptionMethod")!.Add(carried);
        return copy;
    }

    // A framework envelope under B whose plaintext carries a key wrapped to C, named by issuer
    // and serial only, and whose EncryptionMethod hides a second EncryptedData that reaches that
    // key by reference. Decrypting the outer element decrypts the inner one too.
    private static XElement NestedEncryptedDataKeyedByC(XElement key)
    {
        var copy = new XElement(key);
        var outer = copy.Descendants(Xenc + "EncryptedData").Single();
        XElement clear;
        using (var services = new ServiceCollection().AddDataProtection()
            .UnprotectKeysWithAnyCertificate(B.Load()).Services.BuildServiceProvider())
            clear = new EncryptedXmlDecryptor(services).Decrypt(outer);

        using var aes = Aes.Create();
        using var c = C.Load();
        var wrapped = new EncryptedKey
        {
            Id = "wrapped-1420",
            EncryptionMethod = new EncryptionMethod(EncryptedXml.XmlEncRSAOAEPUrl),
            CipherData = new CipherData(EncryptedXml.EncryptKey(aes.Key, c.GetRSAPublicKey()!, useOAEP: true)),
        };
        var wrappedXml = XElement.Parse(wrapped.GetXml().OuterXml);
        wrappedXml.Add(new XElement(Dsig + "KeyInfo", new XElement(Dsig + "X509Data", IssuerSerial(C))));
        clear.Add(wrappedXml);

        var inner = new System.Xml.XmlDocument();
        inner.LoadXml("<inner>not-key-material</inner>");
        var nested = new EncryptedData
        {
            Type = EncryptedXml.XmlEncElementUrl,
            EncryptionMethod = new EncryptionMethod(EncryptedXml.XmlEncAES256Url),
            CipherData = new CipherData(new EncryptedXml().EncryptData(inner.DocumentElement!, aes, false)),
            KeyInfo = new KeyInfo(),
        };
        nested.KeyInfo.AddClause(new KeyInfoRetrievalMethod("#wrapped-1420", EncryptedXml.XmlEncEncryptedKeyUrl));

        XElement rewrapped;
        using (var b = B.Load())
            rewrapped = new CertificateXmlEncryptor(b, NullLoggerFactory.Instance).Encrypt(clear).EncryptedElement;
        rewrapped.Element(Xenc + "EncryptionMethod")!.Add(XElement.Parse(nested.GetXml().OuterXml));
        outer.ReplaceWith(rewrapped);
        return copy;
    }

    private static XElement IssuerSerial(Pair pair)
    {
        using var certificate = pair.Load();
        return new XElement(Dsig + "X509IssuerSerial",
            new XElement(Dsig + "X509IssuerName", certificate.IssuerName.Name),
            new XElement(Dsig + "X509SerialNumber", new BigInteger(certificate.GetSerialNumber(), isUnsigned: true).ToString()));
    }

    private static string WithoutSecrets(XElement key)
    {
        var copy = new XElement(key);
        copy.Descendants(Dp + "encryptedSecret").Remove();
        return copy.ToString(SaveOptions.DisableFormatting);
    }

    private static string OutsideCiphertext(string xml)
    {
        var copy = XElement.Parse(xml);
        foreach (var encrypted in copy.Descendants(Xenc + "EncryptedData").ToList())
            encrypted.ReplaceWith(new XElement("ciphertext"));
        return copy.ToString(SaveOptions.DisableFormatting);
    }

    private static IEnumerable<string> EmbeddedFingerprints(string xml) =>
        XElement.Parse(xml).Descendants(Dsig + "X509Certificate").Select(e =>
        {
            using var certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(e.Value));
            return certificate.GetCertHashString(HashAlgorithmName.SHA256);
        }).ToList();

    private static string KeyId(string xml) => KeyId(XElement.Parse(xml));

    private static string KeyId(XElement key) => (string)key.Attribute("id")!;

    private Task<(int ExitCode, string Stdout, string Stderr)> RewrapAsync(
        Pair? current, Pair[] previous, string environment = "Production", Action<ProcessStartInfo>? configure = null)
    {
        var psi = new ProcessStartInfo("dotnet", $"\"{ApiDllPath}\" rewrap-key-ring")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment.Clear();
        foreach (var name in new[] { "PATH", "HOME", "DOTNET_ROOT", "TMPDIR", "LANG", "LC_ALL", "USER" })
            if (Environment.GetEnvironmentVariable(name) is { } value)
                psi.Environment[name] = value;
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = environment;
        psi.Environment["ConnectionStrings__Default"] = database.ConnectionString;
        psi.Environment["Database__Provider"] = "Postgres";
        psi.Environment["Database__AllowInsecureConnection"] = "true";
        if (current is not null)
        {
            psi.Environment["DataProtection__CertificatePem"] = current.CertificatePem;
            psi.Environment["DataProtection__PrivateKeyPem"] = current.PrivateKeyPem;
        }
        for (var i = 0; i < previous.Length; i++)
        {
            psi.Environment[$"DataProtection__PreviousCertificates__{i}__CertificatePem"] = previous[i].CertificatePem;
            psi.Environment[$"DataProtection__PreviousCertificates__{i}__PrivateKeyPem"] = previous[i].PrivateKeyPem;
        }
        configure?.Invoke(psi);
        return SeedCommandRunner.RunToCompletionAsync(Process.Start(psi)!, TimeSpan.FromSeconds(120));
    }

    private async Task InsertAsync(IEnumerable<(string FriendlyName, XElement Xml)> rows)
    {
        foreach (var (friendlyName, xml) in rows)
            await InsertRawAsync(friendlyName, xml.ToString(SaveOptions.DisableFormatting));
    }

    private async Task InsertRawAsync(string friendlyName, string? xml)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var insert = new NpgsqlCommand(
            "INSERT INTO \"DataProtectionKeys\" (\"FriendlyName\", \"Xml\") VALUES (@name, @xml)", connection);
        insert.Parameters.AddWithValue("name", friendlyName);
        insert.Parameters.AddWithValue("xml", (object?)xml ?? DBNull.Value);
        await insert.ExecuteNonQueryAsync();
    }

    private async Task<List<Row>> RowsAsync()
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var select = new NpgsqlCommand(
            "SELECT \"Id\", \"FriendlyName\", \"Xml\" FROM \"DataProtectionKeys\" ORDER BY \"Id\"", connection);
        await using var reader = await select.ExecuteReaderAsync();
        var rows = new List<Row>();
        while (await reader.ReadAsync())
            rows.Add(new(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        return rows;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private sealed record Row(int Id, string? FriendlyName, string? Xml);

    private sealed record Pair(string CertificatePem, string PrivateKeyPem)
    {
        public X509Certificate2 Load() =>
            X509Certificate2.CreateFromPem(PemKey.Normalize(CertificatePem), PemKey.Normalize(PrivateKeyPem));

        public string Sha256
        {
            get
            {
                using var certificate = X509Certificate2.CreateFromPem(PemKey.Normalize(CertificatePem));
                return certificate.GetCertHashString(HashAlgorithmName.SHA256);
            }
        }
    }

    private sealed class MemoryRepository(IEnumerable<Row>? rows = null) : IXmlRepository
    {
        public List<(string FriendlyName, XElement Xml)> Rows { get; } =
            [.. (rows ?? []).Select(r => (r.FriendlyName ?? "", XElement.Parse(r.Xml!)))];

        public XElement Key(Guid keyId) => Rows.Single(r => r.Xml.Name == "key" && KeyId(r.Xml) == keyId.ToString()).Xml;

        public IReadOnlyCollection<XElement> GetAllElements() => [.. Rows.Select(r => new XElement(r.Xml))];

        public void StoreElement(XElement element, string friendlyName) => Rows.Add((friendlyName, new XElement(element)));
    }

    public sealed class Database : IAsyncLifetime
    {
        private readonly SharedPostgresDatabase _database = new(migrated: true);

        public string ConnectionString => _database.GetConnectionString();

        public Task InitializeAsync() => _database.StartAsync();

        public Task DisposeAsync() => _database.DisposeAsync().AsTask();
    }
}
