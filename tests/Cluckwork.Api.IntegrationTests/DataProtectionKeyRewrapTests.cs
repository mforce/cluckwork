using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
            AddSecondSecret(ring.Key(keys.CreateNewKey(now.AddDays(-20), now.AddDays(70)).KeyId), A);
        }
        await InsertAsync(ring.Rows);
        var before = await RowsAsync();
        Assert.Equal(6, before.Count);

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
            Assert.Contains($"{KeyId(old.Xml!)}\t{A.Sha256}\t{B.Sha256}", stdout, StringComparison.Ordinal);
        }
        Assert.Empty(PlaintextDataProtectionKeyGuard.PlaintextKeyIds(after.Select(r => r.Xml)));
        Assert.Contains($"current certificate SHA-256 {B.Sha256}", stdout, StringComparison.Ordinal);
        Assert.Contains("rewrapped 5, already current 0, revocations 1", stdout, StringComparison.Ordinal);
        AssertNoKeyMaterial(stdout, stderr);

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
        Assert.Contains("rewrapped 0, already current 5, revocations 1", rerun.Stdout, StringComparison.Ordinal);
        AssertNoKeyMaterial(rerun.Stdout, rerun.Stderr);
        Assert.Equal(after, await RowsAsync());

        var withoutPrevious = await RewrapAsync(B, []);
        Assert.True(withoutPrevious.ExitCode == 0, $"check exit {withoutPrevious.ExitCode}\nstderr={withoutPrevious.Stderr}");
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

    [Theory]
    [InlineData("unknown decryptor type")]
    [InlineData("plaintext key")]
    [InlineData("key without encryptedSecret")]
    [InlineData("unknown root")]
    [InlineData("empty Xml")]
    public async Task A_row_the_verb_cannot_classify_refuses_the_run(string shape)
    {
        var rows = KeysUnder(A, 2);
        var odd = rows[1].Xml;
        string? xml = shape switch
        {
            "unknown decryptor type" => WithDecryptorType(
                odd, "Microsoft.AspNetCore.DataProtection.XmlEncryption.DpapiNGXmlDecryptor, Microsoft.AspNetCore.DataProtection"),
            "plaintext key" =>
                "<key id=\"00000000-0000-0000-0000-000000001420\" version=\"1\"><descriptor><descriptor>"
                + "<masterKey p4:requiresEncryption=\"true\" xmlns:p4=\"http://schemas.asp.net/2015/03/dataProtection\">"
                + "<value>not-a-real-key</value></masterKey></descriptor></descriptor></key>",
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
    // but not in the configuration must still refuse. The store lives under a scratch HOME.
    [Fact]
    public async Task A_recipient_only_the_certificate_store_holds_refuses_the_run()
    {
        await InsertAsync(KeysUnder(C, 1));
        var before = await RowsAsync();
        var home = Directory.CreateTempSubdirectory("cluckwork-1420-home-");
        try
        {
            var store = Directory.CreateDirectory(
                Path.Combine(home.FullName, ".dotnet", "corefx", "cryptography", "x509stores", "my"));
            using (var c = C.Load())
                await File.WriteAllBytesAsync(Path.Combine(store.FullName, $"{c.Thumbprint}.pfx"), c.Export(X509ContentType.Pkcs12));

            var (exitCode, stdout, stderr) = await RewrapAsync(
                B, [], configure: psi => psi.Environment["HOME"] = home.FullName);

            Assert.True(exitCode == 1, $"exit {exitCode}\nstdout={stdout}\nstderr={stderr}");
            Assert.Contains(C.Sha256, stderr, StringComparison.Ordinal);
            Assert.Equal(before, await RowsAsync());
        }
        finally
        {
            home.Delete(recursive: true);
        }
    }

    private static void AssertNoKeyMaterial(string stdout, string stderr)
    {
        Assert.DoesNotContain("masterKey", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("masterKey", stderr, StringComparison.Ordinal);
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

    // A second encrypted element ahead of the master key's: the framework decrypts both.
    private static void AddSecondSecret(XElement key, Pair recipient)
    {
        var secret = key.Descendants(Dp + "encryptedSecret").Single();
        using var certificate = recipient.Load();
        var encrypted = new CertificateXmlEncryptor(certificate, NullLoggerFactory.Instance)
            .Encrypt(new XElement("note", "not-key-material")).EncryptedElement;
        secret.Parent!.AddFirst(new XElement(secret.Name, secret.Attributes(), encrypted));
    }

    private static string WithDecryptorType(XElement key, string decryptorType)
    {
        var copy = new XElement(key);
        copy.Descendants(Dp + "encryptedSecret").Single().SetAttributeValue("decryptorType", decryptorType);
        return copy.ToString(SaveOptions.DisableFormatting);
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
