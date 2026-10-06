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

        return services;
    }

    // #794 — every serving replica reads one key ring from the database they already
    // share, so a token one instance protects validates on another and after a restart.
    // One-shot verbs mint and redeem in the same process (recover-admin's reset token),
    // so they keep an in-memory ring and never write a key to the shared table.
    public static IServiceCollection AddCluckworkDataProtection(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        ProcessRole role)
    {
        var dataProtection = services.AddDataProtection().SetApplicationName("Cluckwork");
        if (role is not ProcessRole.Serving)
        {
            dataProtection.UseEphemeralDataProtectionProvider();
            return services;
        }

        dataProtection.PersistKeysToDbContext<AppDbContext>();
        if (EnsureKeyEncryptionCertificate(configuration, environment) is { } certificate)
            dataProtection.ProtectKeysWithCertificate(certificate);

        return services;
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
                "DataProtection:CertificatePem and DataProtection:PrivateKeyPem are not a usable "
                + "certificate and matching private key.", ex);
        }

        // The key ring's XML encryption supports RSA only; an EC key would boot and then
        // fail the first time a key is written.
        if (certificate.GetRSAPrivateKey() is null)
            throw new InvalidOperationException(
                "DataProtection:PrivateKeyPem is not an RSA key. The key ring's XML encryption "
                + "needs an RSA certificate.");

        return certificate;
    }
}
