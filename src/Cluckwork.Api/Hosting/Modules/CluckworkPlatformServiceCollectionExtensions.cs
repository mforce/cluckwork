using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Cluckwork.Api.Middleware;
using Cluckwork.Application.Common;
using Cluckwork.Infrastructure.Modules.Access.Identity;
using Cluckwork.Infrastructure.Modules.Access.Repositories;
using Cluckwork.Infrastructure.Persistence;
using Cluckwork.Infrastructure.Repositories;
using Cluckwork.Infrastructure.Time;
using FluentValidation;
using Microsoft.AspNetCore.DataProtection;

namespace Cluckwork.Api.Hosting.Modules;

internal static class CluckworkPlatformServiceCollectionExtensions
{
    public static IServiceCollection AddCluckworkPlatformServices(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddScoped<IFlockScopeGuard, FlockScopeGuard>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<ICurrentTransaction, CurrentTransaction>();
        services.AddScoped<IClock, SystemClock>();
        services.AddScoped<IFarmClock, FarmClock>();
        services.AddSingleton(TimeProvider.System);
        // #309 — the login DTO validator lives in the Api assembly (it validates
        // the Api LoginRequest). MAX-length only; see LoginRequestValidator.
        services.AddScoped<
            IValidator<Cluckwork.Api.Modules.Access.Auth.LoginRequest>,
            Cluckwork.Api.Modules.Access.Auth.LoginRequestValidator>();
        // #308
        services.AddScoped<
            IValidator<Cluckwork.Api.Modules.Access.Auth.StepUpRequest>,
            Cluckwork.Api.Modules.Access.Auth.StepUpRequestValidator>();

        // #307 — lease duration / max-wait bounds for the idempotency claim protocol.
        services.Configure<IdempotencyOptions>(
            configuration.GetSection(IdempotencyOptions.SectionName));
        services.AddScoped<IdempotentUnit>();

        return services;
    }

    // #794 — every serving replica reads one key ring from the database they already
    // share, so a token one instance protects validates on another and after a restart.
    // One-shot verbs mint and redeem in the same process (recover-admin's reset token),
    // so they keep an in-memory ring and never write a key to the shared table.
    public static CluckworkDataProtectionRegistration AddCluckworkDataProtection(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        ProcessRole role)
    {
        var dataProtection = services.AddDataProtection().SetApplicationName("Cluckwork");
        if (role is not ProcessRole.Serving)
        {
            dataProtection.UseEphemeralDataProtectionProvider();
            return new(CertificateSha256: null, PreviousCertificateSha256s: []);
        }

        dataProtection.PersistKeysToDbContext<AppDbContext>();
        if (environment.IsProduction())
            services.AddHostedService<PlaintextDataProtectionKeyGuard>();
        var certificate = EnsureKeyEncryptionCertificate(configuration, environment);
        if (certificate is not null)
            dataProtection.ProtectKeysWithCertificate(certificate);
        var previous = EnsurePreviousCertificates(configuration, certificate);
        dataProtection.UnprotectKeysWithAnyCertificate(previous);

        return new(
            certificate?.GetCertHashString(HashAlgorithmName.SHA256),
            [.. previous.Select(c => c.GetCertHashString(HashAlgorithmName.SHA256))]);
    }

    // Production refuses to store the key ring in plaintext: anyone holding a database
    // backup could otherwise forge what it protects. Elsewhere the certificate is optional.
    private static X509Certificate2? EnsureKeyEncryptionCertificate(
        IConfiguration configuration, IHostEnvironment environment)
    {
        var certificatePem = configuration["DataProtection:CertificatePem"];
        var privateKeyPem = configuration["DataProtection:PrivateKeyPem"];
        if (string.IsNullOrWhiteSpace(certificatePem) && string.IsNullOrWhiteSpace(privateKeyPem))
        {
            if (environment.IsProduction())
                throw new InvalidOperationException(
                    "DataProtection:CertificatePem and DataProtection:PrivateKeyPem are not configured. "
                    + "Production encrypts the Data Protection key ring at rest with this certificate, "
                    + "so the API refuses to start rather than store the keys in plaintext.");
            return null;
        }

        return EnsureUsableCertificate(
            certificatePem, privateKeyPem,
            "DataProtection:CertificatePem and DataProtection:PrivateKeyPem are", "DataProtection:PrivateKeyPem");
    }

    // #1338 — certificates the ring was encrypted with before the current one, so a planned
    // change keeps stored keys readable. The framework decrypts with them and never encrypts.
    // A leaked certificate never belongs here: that revokes the ring (794). Outside Production
    // a list with no current certificate is harmless: it only reads, and new keys stay plaintext
    // as they already do there.
    private static X509Certificate2[] EnsurePreviousCertificates(
        IConfiguration configuration, X509Certificate2? current) =>
    [
        .. configuration.GetSection("DataProtection:PreviousCertificates").GetChildren().Select(entry =>
        {
            var certificate = EnsureUsableCertificate(
                entry["CertificatePem"], entry["PrivateKeyPem"], $"{entry.Path} is", entry.Path);
            // Thumbprint only: a renewal on the same key pair is a different certificate and boots.
            if (current is not null && certificate.Thumbprint == current.Thumbprint)
                throw new InvalidOperationException(
                    $"{entry.Path} is the current certificate. List only certificates the key ring "
                    + "used before the current one.");
            return certificate;
        }),
    ];

    // The framework skips a decryption certificate with no private key, so a certificate
    // alone would boot and then fail every read: CreateFromPem refuses it here.
    private static X509Certificate2 EnsureUsableCertificate(
        string? certificatePem, string? privateKeyPem, string pairSubject, string keySubject)
    {
        X509Certificate2 certificate;
        try
        {
            certificate = X509Certificate2.CreateFromPem(
                PemKey.Normalize(certificatePem ?? string.Empty),
                PemKey.Normalize(privateKeyPem ?? string.Empty));
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            throw new InvalidOperationException(
                $"{pairSubject} not a usable certificate and matching private key.", ex);
        }

        // The key ring's XML encryption supports RSA only; an EC key would boot and then
        // fail the first time a key is written, or, as a previous certificate, every read.
        if (certificate.GetRSAPrivateKey() is null)
            throw new InvalidOperationException(
                $"{keySubject} is not an RSA key. The key ring's XML encryption needs an RSA certificate.");

        return certificate;
    }
}

// Registration runs before a logger exists, so Program logs the fingerprints after Build().
internal sealed record CluckworkDataProtectionRegistration(
    string? CertificateSha256, IReadOnlyList<string> PreviousCertificateSha256s)
{
    // #794 — which certificates the key ring uses, so an operator can match them to the
    // ones they issued. The fingerprints only; never the PEM or the key.
    public void LogFingerprints(ILogger logger)
    {
        if (CertificateSha256 is not null)
            logger.LogInformation(
                "Data Protection key ring encrypted with the certificate whose SHA-256 fingerprint is {CertificateSha256}",
                CertificateSha256);
        foreach (var previousSha256 in PreviousCertificateSha256s)
            logger.LogInformation(
                "Data Protection key ring also reads keys encrypted with the previous certificate whose SHA-256 fingerprint is {PreviousCertificateSha256}",
                previousSha256);
    }
}
