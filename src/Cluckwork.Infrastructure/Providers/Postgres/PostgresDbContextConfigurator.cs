namespace Cluckwork.Infrastructure.Providers.Postgres;

using Microsoft.EntityFrameworkCore;

public sealed class PostgresDbContextConfigurator : IDbProviderConfigurator
{
    // Migrations live here so provider-specific SQL stays separate (tech spec §5.2).
    public string MigrationsAssembly => typeof(PostgresDbContextConfigurator).Assembly.GetName().Name!;

    public void Configure(DbContextOptionsBuilder builder, string connectionString)
    {
        builder.UseNpgsql(connectionString, npgsql =>
        {
            npgsql.MigrationsAssembly(MigrationsAssembly);
        });
    }
}
