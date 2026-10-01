using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Infrastructure.Providers;

// Abstraction for provider-specific DbContext configuration (tech spec §5.3).
// Selected at startup from "Database:Provider" config key.
public interface IDbProviderConfigurator
{
    void Configure(
        DbContextOptionsBuilder builder, string connectionString, DatabaseResilienceOptions resilience);
    string MigrationsAssembly { get; }
}
