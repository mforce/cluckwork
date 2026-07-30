namespace Cluckwork.Infrastructure.Providers.Postgres;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

public sealed class PostgresDbContextConfigurator : IDbProviderConfigurator
{
    private readonly bool _isProduction;
    private readonly ILogger<PostgresDbContextConfigurator>? _logger;

    // isProduction gates the #262 TLS floor; the design-time factory and any
    // non-Production host use the defaults (no enforcement, no logger).
    public PostgresDbContextConfigurator(
        bool isProduction = false,
        ILogger<PostgresDbContextConfigurator>? logger = null)
    {
        _isProduction = isProduction;
        _logger = logger;
    }

    // Migrations live here so provider-specific SQL stays separate (tech spec §5.2).
    public string MigrationsAssembly => typeof(PostgresDbContextConfigurator).Assembly.GetName().Name!;

    public void Configure(DbContextOptionsBuilder builder, string connectionString)
    {
        // #261 accept URI-form strings; #262 enforce the Production TLS floor —
        // both applied here, before the string reaches the Npgsql parser.
        var normalized = PostgresConnectionString.NormalizeAndValidate(
            connectionString,
            _isProduction,
            _logger is null ? null : message => _logger.LogWarning("{ConnectionStringWarning}", message));

        builder.UseNpgsql(normalized, npgsql =>
        {
            npgsql.MigrationsAssembly(MigrationsAssembly);
        });
    }
}
