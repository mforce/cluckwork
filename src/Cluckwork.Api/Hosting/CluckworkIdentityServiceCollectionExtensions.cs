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

        services.AddScoped<IIdentityProvider, IdentityProvider>();
        // Break-glass recovery must remain available in Production.
        services.AddScoped<AdminRecoveryService>();
        // #283 — first-run admin provisioning (`bootstrap-admin`), same
        // always-available-in-Production posture as break-glass recovery: a
        // real deploy's first login depends on it.
        services.AddScoped<FirstRunAdminService>();
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
