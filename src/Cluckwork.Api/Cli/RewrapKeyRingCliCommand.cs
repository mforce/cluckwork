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
// rewrites the rows itself and never starts the ring. Every key row is decrypted on every
// run, so a run without a certificate listed is the check that no row still needs it.
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
            var rewrap = new KeyRingRewrap(current, previous);
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
                if (plan.NewXml is not null)
                    row.Xml = plan.NewXml;
            if (PlaintextDataProtectionKeyGuard.PlaintextKeyIds(plans.Select(p => p.Plan.NewXml)).Count > 0)
                throw new InvalidOperationException("a re-wrapped key would be stored unencrypted. No row was written.");
            await db.SaveChangesAsync();

            foreach (var (_, plan) in plans)
                await Console.Out.WriteLineAsync(plan.Line);
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
            // database, the encryptor or this verb's own check, whose messages carry no key XML.
            await Console.Error.WriteLineAsync($"rewrap-key-ring failed: {ex.GetBaseException().Message}");
            return 1;
        }
    }
}

internal enum RowOutcome { Revocation, Current, Rewrapped, Refused }

internal sealed record RowPlan(RowOutcome Outcome, string Line, string? NewXml = null);

// Replaces only the EncryptedData under each encryptedSecret. Everything else in the row,
// decryptorType included, stays as stored.
internal sealed class KeyRingRewrap
{
    private static readonly XNamespace Dp = "http://schemas.asp.net/2015/03/dataProtection";
    private static readonly XNamespace Xenc = "http://www.w3.org/2001/04/xmlenc#";
    private static readonly XNamespace Dsig = "http://www.w3.org/2000/09/xmldsig#";
    private static readonly Type Decryptor = typeof(EncryptedXmlDecryptor);

    private readonly EncryptedXmlDecryptor _decryptor;
    private readonly CertificateXmlEncryptor _encryptor;
    private readonly HashSet<string> _configured;

    public KeyRingRewrap(X509Certificate2 current, X509Certificate2[] previous)
    {
        CurrentSha256 = Sha256(current);
        PreviousSha256s = [.. previous.Select(Sha256)];
        _configured = [CurrentSha256, .. PreviousSha256s];
        // XmlKeyDecryptionOptions is internal; the decryptor's constructor reads it from these services.
        using (var services = new ServiceCollection().AddDataProtection()
            .UnprotectKeysWithAnyCertificate([current, .. previous]).Services.BuildServiceProvider())
            _decryptor = new EncryptedXmlDecryptor(services);
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
            if (!IsEncryptedXmlDecryptor((string?)secret.Attribute("decryptorType")))
                return Refuse(row, keyId, "names a decryptor other than EncryptedXmlDecryptor");
            if (Recipient(secret) is not ({ } data, { } recipient))
                return Refuse(row, keyId, "is not in the envelope the framework writes");
            before.Add(recipient);
            // Before the decrypt: EncryptedXml falls back to the OS certificate store, which
            // must not rescue a row no configured certificate can read.
            if (!_configured.Contains(recipient))
                return Refuse(row, keyId, $"is encrypted with the certificate {recipient}, which is not configured");
            XElement clear;
            try
            {
                clear = _decryptor.Decrypt(data);
            }
            catch (Exception)
            {
                return Refuse(row, keyId, $"does not decrypt with the certificate {recipient}");
            }
            if (recipient == CurrentSha256)
                continue;
            data.ReplaceWith(_encryptor.Encrypt(clear).EncryptedElement);
            rewritten = true;
        }

        var line = $"{keyId}\t{string.Join(",", before.Distinct())}\t{CurrentSha256}";
        return rewritten
            ? new(RowOutcome.Rewrapped, line, root.ToString(SaveOptions.DisableFormatting))
            : new(RowOutcome.Current, line);
    }

    // The framework resolves the stored type name and ignores the assembly version, so a ring
    // an older framework wrote names another version. A name it could not resolve refuses.
    private static bool IsEncryptedXmlDecryptor(string? decryptorType) =>
        decryptorType?.Split(',', StringSplitOptions.TrimEntries) is [var type, var assembly, ..]
        && type == Decryptor.FullName
        && assembly == Decryptor.Assembly.GetName().Name;

    // The direct envelope CertificateXmlEncryptor writes, one of each element and nothing
    // beside them in either KeyInfo: EncryptedData/KeyInfo/EncryptedKey/KeyInfo/X509Data/
    // X509Certificate, with no EncryptedData nested inside and no CipherReference. Decrypting
    // also decrypts a nested EncryptedData, whose own KeyInfo could reach the OS certificate
    // store; a CipherReference makes the decrypt resolve a URI.
    private static (XElement Data, string Sha256)? Recipient(XElement secret)
    {
        if (OnlyChild(secret, Xenc + "EncryptedData") is not { } data
            || data.Descendants().Any(e => e.Name == Xenc + "EncryptedData" || e.Name == Xenc + "CipherReference")
            || OnlyChild(data.Element(Dsig + "KeyInfo"), Xenc + "EncryptedKey") is not { } encryptedKey
            || OnlyChild(encryptedKey.Element(Dsig + "KeyInfo"), Dsig + "X509Data") is not { } x509Data
            || OnlyChild(x509Data, Dsig + "X509Certificate") is not { } embedded)
            return null;
        try
        {
            using var certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(embedded.Value));
            return (data, Sha256(certificate));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static XElement? OnlyChild(XElement? parent, XName name) =>
        parent?.Elements().ToList() is [{ } child] && child.Name == name ? child : null;

    private static string Sha256(X509Certificate2 certificate) => certificate.GetCertHashString(HashAlgorithmName.SHA256);

    private static RowPlan Refuse(DataProtectionKey row, string? keyId, string reason) =>
        new(RowOutcome.Refused, keyId is null ? $"row {row.Id} {reason}." : $"row {row.Id} (key {keyId}) {reason}.");
}
