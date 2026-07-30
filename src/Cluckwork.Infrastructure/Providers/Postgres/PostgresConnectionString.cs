namespace Cluckwork.Infrastructure.Providers.Postgres;

using Microsoft.AspNetCore.WebUtilities;
using Npgsql;

// Normalizes a Postgres connection string and enforces the production TLS floor,
// applied once before the string reaches UseNpgsql.
//
// #261 — accepts libpq URI form (postgresql://user:pass@host:5432/db?sslmode=...) in
//        addition to Npgsql key-value form. Many managed-Postgres platforms emit a
//        URI, and Npgsql's own parser only understands key-value and throws on a URI.
// #262 — in Production only, rejects a TLS mode weaker than Require (an unencrypted
//        connection is a plaintext/MITM risk) and, when only Require is set, warns that
//        VerifyFull with a host CA is preferred. It never auto-injects or upgrades a mode.
internal static class PostgresConnectionString
{
    // Both spellings are valid libpq URI schemes (PostgreSQL docs §34.1.1.2).
    private static readonly string[] UriSchemes = ["postgres", "postgresql"];

    private const int DefaultPort = 5432;

    // libpq query-parameter names whose Npgsql keyword differs. `sslmode` is handled
    // separately (it is an enum whose libpq spellings Npgsql's parser rejects); these
    // are plain string keywords the Npgsql builder understands under a different name.
    private static readonly Dictionary<string, string> LibpqParameterToNpgsqlKeyword =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["sslrootcert"] = "Root Certificate",
            ["sslcert"] = "SSL Certificate",
            ["sslkey"] = "SSL Key",
            ["dbname"] = "Database",
        };

    /// <summary>
    /// Returns a key-value connection string (translating URI form when needed) and, in
    /// Production, enforces the TLS floor: throws for a mode weaker than Require, and
    /// invokes <paramref name="onWarning"/> when only Require (not VerifyFull/VerifyCA) is set.
    /// </summary>
    public static string NormalizeAndValidate(
        string connectionString,
        bool isProduction,
        Action<string>? onWarning = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var normalized = IsUri(connectionString)
            ? ConvertUriToKeyValue(connectionString)
            : connectionString;

        if (isProduction)
        {
            EnforceTlsFloor(normalized, onWarning);
        }

        return normalized;
    }

    private static bool IsUri(string connectionString) =>
        Uri.TryCreate(connectionString, UriKind.Absolute, out var uri)
        && UriSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase);

    private static string ConvertUriToKeyValue(string uriString)
    {
        var uri = new Uri(uriString);

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            // System.Uri yields Port == -1 when the authority omits the port.
            Port = uri.Port < 0 ? DefaultPort : uri.Port,
        };

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            // Split on the FIRST ':' only — a decoded ':' inside the password must not
            // be treated as the separator; decode each half afterwards.
            var credentials = uri.UserInfo.Split(':', 2);
            builder.Username = Uri.UnescapeDataString(credentials[0]);
            if (credentials.Length == 2)
            {
                builder.Password = Uri.UnescapeDataString(credentials[1]);
            }
        }

        var database = uri.AbsolutePath.TrimStart('/');
        if (!string.IsNullOrEmpty(database))
        {
            builder.Database = Uri.UnescapeDataString(database);
        }

        foreach (var (key, values) in QueryHelpers.ParseQuery(uri.Query))
        {
            ApplyQueryParameter(builder, key, values.ToString());
        }

        return builder.ConnectionString;
    }

    private static void ApplyQueryParameter(NpgsqlConnectionStringBuilder builder, string key, string value)
    {
        if (string.Equals(key, "sslmode", StringComparison.OrdinalIgnoreCase))
        {
            builder.SslMode = ParseSslMode(value);
            return;
        }

        var keyword = LibpqParameterToNpgsqlKeyword.GetValueOrDefault(key, key);
        builder[keyword] = value;
    }

    private static SslMode ParseSslMode(string value)
    {
        // libpq spells modes "verify-ca"/"verify-full"; Npgsql's enum parse rejects the
        // hyphen/underscore forms, so strip separators before matching the enum names.
        var canonical = value.Replace("-", string.Empty).Replace("_", string.Empty);
        if (!Enum.TryParse<SslMode>(canonical, ignoreCase: true, out var sslMode))
        {
            throw new InvalidOperationException(
                $"Unrecognized sslmode '{value}' in the Postgres connection URI. Valid values: " +
                "disable, allow, prefer, require, verify-ca, verify-full.");
        }

        return sslMode;
    }

    private static void EnforceTlsFloor(string keyValueConnectionString, Action<string>? onWarning)
    {
        var sslMode = new NpgsqlConnectionStringBuilder(keyValueConnectionString).SslMode;

        switch (sslMode)
        {
            case SslMode.Disable:
            case SslMode.Allow:
            case SslMode.Prefer:
                throw new InvalidOperationException(
                    $"Production database connections must use TLS: sslmode='{sslMode}' permits an " +
                    "unencrypted connection (plaintext/MITM risk). Set sslmode=Require at minimum " +
                    "(sslmode=VerifyFull with a host CA is strongly preferred).");
            case SslMode.Require:
                onWarning?.Invoke(
                    "Production database connection uses sslmode=Require, which encrypts the connection " +
                    "but does not verify the server certificate. Prefer sslmode=VerifyFull with a host CA " +
                    "to defend against MITM.");
                break;

            // VerifyCA / VerifyFull — certificate-validated TLS, nothing to enforce.
        }
    }
}
