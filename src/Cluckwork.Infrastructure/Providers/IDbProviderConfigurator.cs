namespace Cluckwork.Infrastructure.Providers;

using Microsoft.EntityFrameworkCore;

// Abstraction for provider-specific DbContext configuration (tech spec §5.3).
// Selected at startup from "Database:Provider" config key.
public interface IDbProviderConfigurator
{
    void Configure(
        DbContextOptionsBuilder builder, string connectionString, DatabaseResilienceOptions resilience);
    string MigrationsAssembly { get; }
}
