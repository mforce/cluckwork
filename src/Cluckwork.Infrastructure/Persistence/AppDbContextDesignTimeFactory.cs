namespace Cluckwork.Infrastructure.Persistence;

using Cluckwork.Infrastructure.Persistence.Interceptors;
using Cluckwork.Infrastructure.Providers.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

public sealed class AppDbContextDesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        var connectionString = Environment.GetEnvironmentVariable("CLUCKWORK_MIGRATIONS_CONNECTION")
            ?? "Host=localhost;Database=cluckwork_migrations;Username=postgres;Password=postgres";

        // Design-time (dotnet ef) is never Production — normalize for URI support (#261)
        // but skip the TLS floor. Key-value strings pass through unchanged.
        var normalized = PostgresConnectionString.NormalizeAndValidate(connectionString, isProduction: false);
        new PostgresDbContextConfigurator().Configure(options, normalized);
        options.AddInterceptors(new TenantStampInterceptor(new TenantContext()));

        return new AppDbContext(options.Options, new TenantContext());
    }
}
