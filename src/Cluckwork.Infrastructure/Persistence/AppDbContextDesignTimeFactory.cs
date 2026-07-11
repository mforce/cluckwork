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

        new PostgresDbContextConfigurator().Configure(options, connectionString);
        options.AddInterceptors(new TenantStampInterceptor(new TenantContext()));

        return new AppDbContext(options.Options, new TenantContext());
    }
}
