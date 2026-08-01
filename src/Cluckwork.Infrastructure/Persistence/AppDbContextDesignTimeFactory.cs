namespace Cluckwork.Infrastructure.Persistence;

using System.Net;
using Cluckwork.Infrastructure.Persistence.Interceptors;
using Cluckwork.Infrastructure.Providers.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql;

// #318 — `dotnet ef` design-time tooling fails closed. There is no default connection: an
// unset/blank CLUCKWORK_MIGRATIONS_CONNECTION used to fall back to a predictable
// Host=localhost;...;Username=postgres;Password=postgres, letting a typo silently target the
// wrong database and normalizing a known credential. Every target is now held to the SAME
// allow-list TLS floor a Production boot enforces (#261/#262: VerifyCA/VerifyFull silent,
// Require warns, everything else fails) — reusing PostgresConnectionString.NormalizeAndValidate
// rather than a second, divergent validator — except an explicitly acknowledged LOOPBACK
// development target, which may opt into plaintext (mirrors the shape of the Production
// Database:AllowInsecureConnection escape hatch, but scoped to loopback only).
public sealed class AppDbContextDesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public const string ConnectionEnvVar = "CLUCKWORK_MIGRATIONS_CONNECTION";
    public const string AllowInsecureLoopbackEnvVar = "CLUCKWORK_MIGRATIONS_ALLOW_INSECURE_LOOPBACK";

    public AppDbContext CreateDbContext(string[] args)
    {
        var rawConnectionString = Environment.GetEnvironmentVariable(ConnectionEnvVar);
        if (string.IsNullOrWhiteSpace(rawConnectionString))
        {
            throw new InvalidOperationException(
                $"Design-time migrations require an explicit connection. Set the " +
                $"'{ConnectionEnvVar}' environment variable to a Postgres connection string " +
                "(Npgsql key-value, e.g. 'Host=...;Database=...;Username=...;Password=...', " +
                "or a postgresql://user:pass@host/db URI) before running dotnet ef — there is " +
                "no default target.");
        }

        // Pass 1: translate a libpq URI to key-value (#261) without enforcing the TLS floor
        // yet — the floor below needs the resolved Host to decide whether the loopback opt-in
        // applies.
        var normalized = PostgresConnectionString.NormalizeAndValidate(
            rawConnectionString, isProduction: false);
        var host = new NpgsqlConnectionStringBuilder(normalized).Host;
        var loopbackOptIn = IsEnvFlagSet(AllowInsecureLoopbackEnvVar);

        if (loopbackOptIn && !IsLoopbackHost(host))
        {
            throw new InvalidOperationException(
                $"'{AllowInsecureLoopbackEnvVar}' only permits an insecure connection to a " +
                $"loopback host (localhost/127.0.0.1/::1). '{ConnectionEnvVar}' targets host " +
                $"'{host}', which is not loopback — use a TLS-secured connection " +
                "(sslmode=verify-full recommended) for a non-loopback migrations target.");
        }

        // Pass 2: enforce the SAME allow-list TLS floor as a Production boot. When the
        // loopback opt-in above applies, plaintext to that loopback host is permitted with a
        // loud warning instead of failing.
        var warnings = new List<string>();
        PostgresConnectionString.NormalizeAndValidate(
            normalized,
            isProduction: true,
            allowInsecureConnection: loopbackOptIn,
            onWarning: warnings.Add);
        foreach (var warning in warnings)
        {
            // The shared validator names Database:AllowInsecureConnection — the control
            // that governs a BOOTING host. Design-time tooling never reads it; the
            // acknowledgement that actually applies here is the env var below. Printing
            // the borrowed text verbatim would send an operator to a setting that does
            // nothing for `dotnet ef`, so name the one that does.
            Console.Error.WriteLine(
                "warning: " + warning.Replace(
                    "Database:AllowInsecureConnection",
                    AllowInsecureLoopbackEnvVar,
                    StringComparison.Ordinal));
        }

        var options = new DbContextOptionsBuilder<AppDbContext>();
        new PostgresDbContextConfigurator().Configure(options, normalized);
        options.AddInterceptors(new TenantStampInterceptor(new TenantContext()));

        return new AppDbContext(options.Options, new TenantContext());
    }

    private static bool IsEnvFlagSet(string variable) =>
        bool.TryParse(Environment.GetEnvironmentVariable(variable), out var flag) && flag;

    private static bool IsLoopbackHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        var trimmed = host.Trim();
        if (trimmed.Length > 1 && trimmed[0] == '[' && trimmed[^1] == ']')
            trimmed = trimmed[1..^1];

        return trimmed.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(trimmed, out var ip) && IPAddress.IsLoopback(ip));
    }
}
