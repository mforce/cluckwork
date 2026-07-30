namespace Cluckwork.Infrastructure.Providers.Postgres;

using Microsoft.EntityFrameworkCore;

public sealed class PostgresDbContextConfigurator : IDbProviderConfigurator
{
    // Migrations live here so provider-specific SQL stays separate (tech spec §5.2).
    public string MigrationsAssembly => typeof(PostgresDbContextConfigurator).Assembly.GetName().Name!;

    public void Configure(DbContextOptionsBuilder builder, string connectionString)
    {
        // connectionString is expected to be already normalized (URI -> key-value) and
        // TLS-validated by PostgresConnectionString.NormalizeAndValidate at startup
        // (#261/#262) — done ONCE there, not per DbContext resolution.
        builder.UseNpgsql(connectionString, npgsql =>
        {
            npgsql.MigrationsAssembly(MigrationsAssembly);
        });
    }
}
