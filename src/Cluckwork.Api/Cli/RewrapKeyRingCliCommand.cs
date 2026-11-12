using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using Cluckwork.Api.Hosting;
using Cluckwork.Api.Hosting.Modules;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cluckwork.Api.Cli;

// `rewrap-key-ring` (#1420) re-encrypts every stored Data Protection key under the current
// certificate, keeping key ids and key material, so a previous certificate can leave
// DataProtection:PreviousCertificates. The one verb that reads the stored ring (794): it
// rewrites the rows itself and never starts the ring. Every row is decrypted on every run,
// so a run without a certificate listed is the check that no row still needs it.
// All or nothing: every refusal comes before the single SaveChanges.
public sealed class RewrapKeyRingCliCommand : ICliCommand
{
    public string Name => "rewrap-key-ring";

    public async Task<int> RunAsync(WebApplication app, string[] args)
    {
        X509Certificate2 current;
        X509Certificate2[] previous;
        try
        {
            current = CluckworkPlatformServiceCollectionExtensions.EnsureKeyEncryptionCertificate(
                    app.Configuration, app.Environment)
                ?? throw new InvalidOperationException(
                    "DataProtection:CertificatePem and DataProtection:PrivateKeyPem are not configured. "
                    + "rewrap-key-ring encrypts the key ring with the current certificate.");
            previous = CluckworkPlatformServiceCollectionExtensions.EnsurePreviousCertificates(app.Configuration, current);
        }
        catch (InvalidOperationException ex)
        {
            await Console.Error.WriteLineAsync($"rewrap-key-ring refused: {ex.Message}");
            return 1;
        }

        try
        {
            using var rewrap = new KeyRingRewrap(current, previous);
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var rows = await db.DataProtectionKeys.OrderBy(k => k.Id).ToListAsync();
            var plans = rows.Select(row => (Row: row, Plan: rewrap.Plan(row))).ToList();

            await Console.Out.WriteLineAsync($"current certificate SHA-256 {rewrap.CurrentSha256}");
            var refusals = plans.Where(p => p.Plan.Outcome is RowOutcome.Refused).ToList();
            if (refusals.Count > 0)
            {
                foreach (var (_, plan) in refusals)
                    await Console.Error.WriteLineAsync($"rewrap-key-ring refused: {plan.Line}");
                await Console.Error.WriteLineAsync("rewrap-key-ring changed no row.");
                return 1;
            }

            foreach (var (row, plan) in plans)
            {
                await Console.Out.WriteLineAsync(plan.Line);
                if (plan.NewXml is not null)
                    row.Xml = plan.NewXml;
            }
            if (PlaintextDataProtectionKeyGuard.PlaintextKeyIds(rows.Select(r => r.Xml)).Count > 0)
                throw new InvalidOperationException("a re-wrapped key would be stored unencrypted. No row was written.");
            await db.SaveChangesAsync();

            var rewrapped = plans.Count(p => p.Plan.Outcome is RowOutcome.Rewrapped);
            var alreadyCurrent = plans.Count(p => p.Plan.Outcome is RowOutcome.Current);
            var revocations = plans.Count(p => p.Plan.Outcome is RowOutcome.Revocation);
            await Console.Out.WriteLineAsync(
                $"rewrapped {rewrapped}, already current {alreadyCurrent}, revocations {revocations}");
            app.Logger.LogInformation(
                "rewrap-key-ring rewrote {Rewrapped} key(s), {AlreadyCurrent} already current, {Revocations} revocation(s) "
                + "under the certificate whose SHA-256 fingerprint is {CertificateSha256}; listed previous certificates: {PreviousCertificateSha256s}",
                rewrapped, alreadyCurrent, revocations, rewrap.CurrentSha256, rewrap.PreviousSha256s);
            return 0;
        }
        catch (Exception ex)
        {
            // Every decrypt and parse failure is a refusal inside Plan, so what lands here is the
            // database or this verb's own check, whose messages carry no key XML.
            await Console.Error.WriteLineAsync($"rewrap-key-ring failed: {ex.GetBaseException().Message}");
            return 1;
        }
    }
}

internal enum RowOutcome { Revocation, Current, Rewrapped, Refused }

internal sealed record RowPlan(RowOutcome Outcome, string Line, string? NewXml = null);

// Replaces only the EncryptedData under each encryptedSecret. Everything else in the row,
// decryptorType included, stays as stored.
internal sealed class KeyRingRewrap : IDisposable
{
    private static readonly XNamespace Dp = "http://schemas.asp.net/2015/03/dataProtection";
    private static readonly XNamespace Xenc = "http://www.w3.org/2001/04/xmlenc#";
    private static readonly XNamespace Dsig = "http://www.w3.org/2000/09/xmldsig#";

    // The framework resolves the type by name and ignores the assembly version, so a ring an
    // older framework wrote names another version.
    private static readonly string DecryptorTypePrefix = typeof(EncryptedXmlDecryptor).FullName + ",";

    private readonly ServiceProvider _decryption;
    private readonly CertificateXmlEncryptor _encryptor;
    private readonly HashSet<string> _configured;

    public KeyRingRewrap(X509Certificate2 current, X509Certificate2[] previous)
    {
        CurrentSha256 = Sha256(current);
        PreviousSha256s = [.. previous.Select(Sha256)];
        _configured = [CurrentSha256, .. PreviousSha256s];
        // XmlKeyDecryptionOptions is internal; the decryptor reads it from these services.
        _decryption = new ServiceCollection().AddDataProtection()
            .UnprotectKeysWithAnyCertificate([current, .. previous]).Services.BuildServiceProvider();
        _encryptor = new CertificateXmlEncryptor(current, NullLoggerFactory.Instance);
    }

    public string CurrentSha256 { get; }

    public IReadOnlyList<string> PreviousSha256s { get; }

    public RowPlan Plan(DataProtectionKey row)
    {
        if (string.IsNullOrEmpty(row.Xml))
            return Refuse(row, null, "has no XML");
        XElement root;
        try
        {
            root = XElement.Parse(row.Xml);
        }
        catch (Exception)
        {
            return Refuse(row, null, "is not well-formed XML");
        }

        if (root.Name == "revocation")
            return new(RowOutcome.Revocation, $"{(string?)root.Element("key")?.Attribute("id")}\trevocation");
        var keyId = (string?)root.Attribute("id");
        if (root.Name != "key")
            return Refuse(row, keyId, "is neither a key nor a revocation");
        if (PlaintextDataProtectionKeyGuard.PlaintextKeyIds([row.Xml]).Count > 0)
            return Refuse(row, keyId, "holds an unencrypted master key: follow procedure B, never re-wrap it");
        var secrets = root.Descendants(Dp + "encryptedSecret").ToList();
        if (secrets.Count == 0)
            return Refuse(row, keyId, "holds no encryptedSecret");

        var before = new List<string>();
        var rewritten = false;
        foreach (var secret in secrets)
        {
            if (((string?)secret.Attribute("decryptorType"))?.StartsWith(DecryptorTypePrefix, StringComparison.Ordinal) is not true)
                return Refuse(row, keyId, "names a decryptor other than EncryptedXmlDecryptor");
            if (Recipient(secret) is not { } recipient)
                return Refuse(row, keyId, "is not one EncryptedData with one EncryptedKey for one X509Certificate");
            before.Add(recipient);
            // Before the decrypt: EncryptedXml falls back to the OS certificate store, which
            // must not rescue a row no configured certificate can read.
            if (!_configured.Contains(recipient))
                return Refuse(row, keyId, $"is encrypted with the certificate {recipient}, which is not configured");
            try
            {
                var encrypted = secret.Elements().Single();
                var clear = new EncryptedXmlDecryptor(_decryption).Decrypt(encrypted);
                if (recipient == CurrentSha256)
                    continue;
                encrypted.ReplaceWith(_encryptor.Encrypt(clear).EncryptedElement);
                rewritten = true;
            }
            catch (Exception)
            {
                return Refuse(row, keyId, $"does not decrypt with the certificate {recipient}");
            }
        }

        var line = $"{keyId}\t{string.Join(",", before.Distinct())}\t{CurrentSha256}";
        return rewritten
            ? new(RowOutcome.Rewrapped, line, root.ToString(SaveOptions.DisableFormatting))
            : new(RowOutcome.Current, line);
    }

    public void Dispose() => _decryption.Dispose();

    // The shape CertificateXmlEncryptor writes. Anything else could name its key some other
    // way, through the certificate store.
    private static string? Recipient(XElement secret)
    {
        var encrypted = secret.Elements().ToList();
        if (encrypted is not [{ } data] || data.Name != Xenc + "EncryptedData"
            || data.Descendants(Xenc + "EncryptedKey").Count() != 1
            || data.Descendants(Dsig + "X509Data").Count() != 1
            || data.Descendants(Dsig + "X509Certificate").ToList() is not [{ } embedded]
            || embedded.Parent?.Name != Dsig + "X509Data"
            || embedded.Parent.Parent?.Parent?.Name != Xenc + "EncryptedKey")
            return null;
        try
        {
            using var certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(embedded.Value));
            return Sha256(certificate);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Sha256(X509Certificate2 certificate) => certificate.GetCertHashString(HashAlgorithmName.SHA256);

    private static RowPlan Refuse(DataProtectionKey row, string? keyId, string reason) =>
        new(RowOutcome.Refused, keyId is null ? $"row {row.Id} {reason}." : $"row {row.Id} (key {keyId}) {reason}.");
}
