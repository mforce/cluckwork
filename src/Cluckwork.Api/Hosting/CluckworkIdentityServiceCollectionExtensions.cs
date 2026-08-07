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
    public static IServiceCollection AddCluckworkIdentity(
        this IServiceCollection services,
        IConfiguration configuration)
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
        var jwtPublicKeyPem = PemKey.Normalize(configuration["Jwt:PublicKeyPem"]
            ?? throw new InvalidOperationException(
                "Jwt:PublicKeyPem is not configured."));

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                var rsa = RSA.Create();
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

        // #308 — the registry is a SINGLETON: replay tracking and logout
        // epochs must be visible across every request, not scoped per-request
        // like TenantContext/CurrentUserContext. The service itself is scoped
        // (it resolves UserManager, which is scoped).
        services.AddSingleton<IStepUpGrantRegistry, InMemoryStepUpGrantRegistry>();
        services.AddScoped<IStepUpGrantService, StepUpGrantService>();

        return services;
    }
}
