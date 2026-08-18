namespace Cluckwork.Api.Hosting;

using System.Security.Cryptography;
using Cluckwork.Api.Configuration;
using Cluckwork.Api.Middleware;
using Cluckwork.Api.Security;
using Cluckwork.Application.Common;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;

internal static class CluckworkIdentityServiceCollectionExtensions
{
    // role is OneShot for the operator verbs. Nothing here issues or validates a
    // token for them — see the key guard below, which is serving-only for that
    // reason and would otherwise be a fresh instance of the #331 class this file
    // now has to avoid (#347/#510).
    public static IServiceCollection AddCluckworkIdentity(
        this IServiceCollection services,
        IConfiguration configuration,
        ProcessRole role = ProcessRole.Serving)
    {
        services.AddScoped<CurrentUserContext>();
        services.AddScoped<ICurrentUser>(sp =>
            sp.GetRequiredService<CurrentUserContext>());

        // #273 — IdentityProvider resolves the caller's IP for the security-event
        // log lines (login failed, lockout, refresh replay/revocation-failed) via
        // this. The built-in accessor, not a bespoke ambient-context type: it is
        // the framework's own answer to "read the current request from a scoped
        // service that isn't itself in the request pipeline."
        services.AddHttpContextAccessor();

        services
            .AddIdentityCore<ApplicationUser>(options =>
            {
                options.Password.RequiredLength = 12;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<AppDbContext>()
            .AddDefaultTokenProviders();

        services.Configure<JwtOptions>(
            configuration.GetSection(JwtOptions.SectionName));
        // #510 — this guard used to be dead for every real deployment, and the
        // key's CONTENT was never checked at boot at all.
        //
        // `configuration[...] ?? throw` only catches NULL. The shipped
        // appsettings.json carries `"PublicKeyPem": ""`, so a deployment that
        // loses its environment variable gets an EMPTY STRING and sailed past —
        // the guard fired only when appsettings.json was absent entirely, which
        // is not how the image runs. And ImportFromPem sat inside the AddJwtBearer
        // delegate, so a malformed key was a per-request failure: the farm booted,
        // /health/ready went green, the container HEALTHCHECK passed, and every
        // authenticated request 500'd. An orchestrator saw a healthy instance that
        // rejected every login.
        //
        // Both halves are fixed here, at boot, matching the fail-closed stance of
        // the #261/#262 TLS floor and the #264 tzdata canary — a farm with no
        // usable signing key must not pretend to be up.
        var jwtPublicKeyPem = EnsureUsablePublicKey(configuration, role);

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                var rsa = RSA.Create();
                // Still imported here because RSA is not thread-safe and this
                // instance is the one the options hold; the boot-time import
                // above is what decides whether the key is usable at all.
                rsa.ImportFromPem(jwtPublicKeyPem);

                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = configuration["Jwt:Issuer"],
                    ValidateAudience = true,
                    ValidAudience = configuration["Jwt:Audience"],
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new RsaSecurityKey(rsa),
                    ClockSkew = TimeSpan.FromSeconds(30),
                    RoleClaimType = "role"
                };
            });

        services.AddAuthorization(options => options.AddCluckworkPolicies());

        services.AddSingleton<
            Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler,
            ForbiddenProblemResultHandler>();

        // #273 codex review (P1b) — shared LoginFailed/AccountLockedOut emitter
        // for every password oracle (LoginAsync AND StepUpGrantService.IssueAsync).
        // Scoped like the two services that consume it below.
        services.AddScoped<AuthSecurityEventLogger>();
        services.AddScoped<IIdentityProvider, IdentityProvider>();
        // Break-glass recovery must remain available in Production.
        services.AddScoped<AdminRecoveryService>();
        // #283 — first-run admin provisioning (`bootstrap-admin`), same
        // always-available-in-Production posture as break-glass recovery: a
        // real deploy's first login depends on it.
        services.AddScoped<FirstRunAdminService>();
        // #283 follow-up — first-run discoverability for the SPA login page.
        // The latch is a SINGLETON (one observation serves every later request
        // process-wide, so the failed-sign-in path stops touching the database
        // once the DEFAULT ACCOUNT has an Owner — that account specifically,
        // never any Owner anywhere); the service is scoped because it resolves
        // AppDbContext.
        services.AddSingleton<FirstRunProvisioningLatch>();
        services.AddScoped<FirstRunStatusService>();
        services.AddScoped<IJwtTokenService, JwtTokenService>();

        // #338 — the registry is now SCOPED: it holds no in-process state (replay
        // lives in IClaimOnceStore, the logout epoch in ApplicationUser), so it
        // just reaches the scoped AppDbContext directly. Registered via an
        // Infrastructure extension because the concrete type is internal.
        services.AddPersistentStepUpGrantRegistry();
        services.AddScoped<IStepUpGrantService, StepUpGrantService>();

        return services;
    }

    // Serving-only (#347). A one-shot verb neither issues nor validates a token —
    // `recover-admin` resets a password, `bootstrap-admin` creates an Owner,
    // `migrate` applies DDL — so requiring a signing key of them would abort the
    // break-glass path over configuration it does not use. That is precisely the
    // #331 class, and making a NEW guard eager is how it would come back; this is
    // why #510's fix belongs with #347 rather than before it.
    //
    // OneShotVerbMinimalConfigTests pins it from the other side: its environment
    // carries no Jwt:* at all, so a verb that started demanding one goes red.
    private static string EnsureUsablePublicKey(IConfiguration configuration, ProcessRole role)
    {
        var publicKeyPem = configuration["Jwt:PublicKeyPem"];
        if (role is not ProcessRole.Serving)
            return publicKeyPem ?? string.Empty;

        // BOTH keys, because both fail the same way and only one of them was ever
        // looked at here. The private key is read per-request by JwtTokenService
        // and StepUpGrantService, so a corrupt one boots green and then 500s the
        // login it is needed for — the same defect as the public key, one file
        // over.
        EnsureUsable("Jwt:PublicKeyPem", publicKeyPem);
        EnsureUsable("Jwt:PrivateKeyPem", configuration["Jwt:PrivateKeyPem"]);
        return PemKey.Normalize(publicKeyPem!);
    }

    private static void EnsureUsable(string key, string? pem)
    {
        // IsNullOrWhiteSpace, not `?? throw`: the shipped appsettings.json carries
        // an EMPTY string for both keys, so a null check is unreachable in any
        // real deployment and the guard was decorative (#510).
        if (string.IsNullOrWhiteSpace(pem))
            throw new InvalidOperationException(
                $"{key} is not configured. The API cannot sign or validate tokens without it, "
                + "so it refuses to start rather than accept traffic it must reject.");

        using var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(PemKey.Normalize(pem));
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            // Named, and at BOOT. Previously this surfaced per-request from inside
            // the AddJwtBearer delegate: /health/ready stayed green, the container
            // HEALTHCHECK passed, and every authenticated request 500'd — an
            // orchestrator saw a healthy instance that rejected every login.
            throw new InvalidOperationException(
                $"{key} is not a usable PEM key: {ex.Message}", ex);
        }
    }
}
