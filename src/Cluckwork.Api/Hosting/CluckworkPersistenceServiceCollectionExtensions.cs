namespace Cluckwork.Api.Hosting;

using Cluckwork.Application.Common;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Jobs;
using Cluckwork.Infrastructure.Persistence;
using Cluckwork.Infrastructure.Persistence.Interceptors;
using Cluckwork.Infrastructure.Providers;
using Cluckwork.Infrastructure.Providers.Postgres;
using Cluckwork.Infrastructure.Time;
using Microsoft.EntityFrameworkCore;

internal static class CluckworkPersistenceServiceCollectionExtensions
{
    public static CluckworkPersistenceRegistration AddCluckworkPersistence(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.AddScoped<TenantContext>();

        var dbProvider = configuration["Database:Provider"] ?? "Postgres";
        var rawConnectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException(
                "Connection string 'Default' is not configured.");

        // #269 — bound synchronously here (mirrors RateLimitingOptions in
        // CluckworkRateLimitingServiceCollectionExtensions) rather than via
        // IOptions<T>: the resolved value is needed immediately below, inside
        // the AddDbContext configuration delegate.
        var resilience = configuration
            .GetSection(DatabaseResilienceOptions.SectionName)
            .Get<DatabaseResilienceOptions>() ?? new DatabaseResilienceOptions();
        resilience.Validate();

        // Normalize and validate once at startup, not in the per-scope callback.
        var connectionStringWarnings = new List<string>();
        var connectionString = PostgresConnectionString.NormalizeAndValidate(
            rawConnectionString,
            isProduction: environment.IsProduction(),
            allowInsecureConnection:
                configuration.GetValue<bool>("Database:AllowInsecureConnection"),
            onWarning: connectionStringWarnings.Add);

        // #271 — the leader lease opens its own dedicated, non-pooled connection
        // from the same normalised, TLS-floor-validated string the DbContext uses.
        services.AddSingleton(new LeaderLeaseConnectionString(connectionString));

        services.AddScoped<TenantStampInterceptor>();
        services.AddDbContext<AppDbContext>((sp, options) =>
        {
            options.AddInterceptors(sp.GetRequiredService<TenantStampInterceptor>());
            IDbProviderConfigurator configurator = dbProvider switch
            {
                "Postgres" => new PostgresDbContextConfigurator(),
                _ => throw new NotSupportedException(
                    $"Unsupported database provider: {dbProvider}")
            };
            configurator.Configure(options, connectionString, resilience);
        });

        // Farm-local boundaries require IANA tzdata/ICU. Validate the image
        // canary before the host builds. #283 — the second half of this guard
        // (a configured Seed:TimeZoneId provisioning zone) is retired along
        // with the runtime seeder it fed: the default account is now a fixed
        // "UTC" migration literal (baked via raw migrationBuilder.Sql with
        // WHERE NOT EXISTS guards), and a real farm sets its own IANA zone via
        // Settings after first login (Account.UpdateSettings) — no boot-time
        // config value to validate.
        TimeZoneAvailability.EnsureResolvable(
            TimeZoneAvailability.CanaryZoneId,
            "Startup time-zone smoke check");

        // Explicit-command seeders (demo/simulation) share the same
        // persistence graph as the serving process.
        if (!environment.IsProduction())
            services.AddScoped<DemoDataSeeder>();

        services.Configure<SimulationOptions>(
            configuration.GetSection(SimulationOptions.SectionName));
        if (!environment.IsProduction())
            services.AddScoped<SimulationDataSeeder>();

        return new CluckworkPersistenceRegistration(connectionStringWarnings);
    }
}

internal sealed record CluckworkPersistenceRegistration(
    IReadOnlyList<string> ConnectionStringWarnings);
