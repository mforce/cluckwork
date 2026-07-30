namespace Cluckwork.Infrastructure.Providers.Postgres;

using Npgsql;

// Normalizes a Postgres connection string and enforces the production TLS floor.
// Call this ONCE at startup (composition root); the result is a plain Npgsql key-value
// string safe to hand to UseNpgsql on every DbContext resolution.
//
// #261 — accepts libpq/managed URI form (postgres://, postgresql://) in addition to
//        Npgsql key-value; Npgsql's own parser only understands key-value and throws on a
//        URI. Query params with no Npgsql equivalent (channel_binding, target_session_attrs,
//        gssencmode, …) are skipped-with-warning rather than failing the whole connection.
// #262 — in Production, enforces the TLS floor as an ALLOW-LIST (fail closed): only
//        VerifyCA/VerifyFull pass silently and Require passes with a warning; EVERY other
//        value — Disable/Allow/Prefer AND any undefined SslMode (e.g. (SslMode)99 from a
//        raw `SSL Mode=99`) — throws, unless Database:AllowInsecureConnection is explicitly
//        set, in which case it boots with a loud warning. It never auto-injects or upgrades.
public static class PostgresConnectionString
{
    // Both spellings are valid libpq URI schemes (PostgreSQL docs §34.1.1.2).
    private static readonly string[] UriSchemes = ["postgres", "postgresql"];

    private const int DefaultPort = 5432;

    // libpq/managed URI query params whose Npgsql keyword differs (or which Npgsql only
    // accepts under a spaced name — e.g. "application_name" throws, "Application Name" works).
    // sslmode / ssl / cert params are handled explicitly below, not through this map.
    private static readonly Dictionary<string, string> LibpqParameterToNpgsqlKeyword =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["sslrootcert"] = "Root Certificate",
            ["sslcert"] = "SSL Certificate",
            ["sslkey"] = "SSL Key",
            ["dbname"] = "Database",
            ["application_name"] = "Application Name",
            ["connect_timeout"] = "Timeout",
        };

    /// <summary>
    /// Returns a key-value connection string (translating URI form when needed) and, in
    /// Production, enforces the TLS floor. Throws <see cref="InvalidOperationException"/> for
    /// a mode that does not guarantee TLS (unless <paramref name="allowInsecureConnection"/>
    /// is set, which downgrades that to a loud warning); invokes <paramref name="onWarning"/>
    /// for a Require-only mode and for skipped URI parameters.
    /// </summary>
    public static string NormalizeAndValidate(
        string connectionString,
        bool isProduction,
        bool allowInsecureConnection = false,
        Action<string>? onWarning = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var normalized = IsUri(connectionString)
            ? ConvertUriToKeyValue(connectionString, onWarning)
            : connectionString;

        if (isProduction)
        {
            EnforceTlsFloor(normalized, allowInsecureConnection, onWarning);
        }

        return normalized;
    }

    private static bool IsUri(string connectionString) =>
        Uri.TryCreate(connectionString, UriKind.Absolute, out var uri)
        && UriSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase);

    private static string ConvertUriToKeyValue(string uriString, Action<string>? onWarning)
    {
        var uri = new Uri(uriString);

        var builder = new NpgsqlConnectionStringBuilder
        {
            // uri.Host keeps the brackets for an IPv6 literal ([::1]); Npgsql accepts that.
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

        ApplyQueryParameters(builder, uri.Query, onWarning);

        return builder.ConnectionString;
    }

    private static void ApplyQueryParameters(
        NpgsqlConnectionStringBuilder builder, string rawQuery, Action<string>? onWarning)
    {
        var query = ParseQuery(rawQuery);

        // TLS precedence: an explicit sslmode wins over the legacy ssl=true flag.
        if (query.TryGetValue("sslmode", out var sslModeValue))
        {
            builder.SslMode = ParseSslMode(sslModeValue);
        }
        else if (query.TryGetValue("ssl", out var sslValue) && IsTruthy(sslValue))
        {
            builder.SslMode = SslMode.Require;
        }

        foreach (var (key, value) in query)
        {
            if (key.Equals("sslmode", StringComparison.OrdinalIgnoreCase)
                || key.Equals("ssl", StringComparison.OrdinalIgnoreCase))
            {
                continue; // handled above.
            }

            var keyword = LibpqParameterToNpgsqlKeyword.GetValueOrDefault(key, key);
            try
            {
                builder[keyword] = value;
            }
            catch (ArgumentException)
            {
                // A libpq/managed-URL parameter with no Npgsql equivalent (channel_binding,
                // target_session_attrs, gssencmode, keepalives*, …). Skip it rather than
                // fail the whole connection — Npgsql negotiates sensible defaults without it.
                onWarning?.Invoke(
                    $"connection-URI parameter '{key}' has no Npgsql equivalent and was ignored.");
            }
        }
    }

    // RFC 3986 query parse with LAST-WINS on duplicate keys. Deliberately not a form
    // decoder: Uri.UnescapeDataString preserves a literal '+' (a form decoder turns it into
    // a space), and duplicates overwrite rather than comma-join (a comma-join could yield an
    // undefined SslMode such as (SslMode)7).
    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var trimmed = query.StartsWith('?') ? query[1..] : query;

        foreach (var pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var key = separator < 0 ? pair : pair[..separator];
            var value = separator < 0 ? string.Empty : pair[(separator + 1)..];
            result[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value);
        }

        return result;
    }

    private static bool IsTruthy(string value) =>
        value.Equals("true", StringComparison.OrdinalIgnoreCase)
        || value.Equals("1", StringComparison.Ordinal)
        || value.Equals("on", StringComparison.OrdinalIgnoreCase)
        || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
        || value.Equals("require", StringComparison.OrdinalIgnoreCase);

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

    private static void EnforceTlsFloor(
        string keyValueConnectionString, bool allowInsecureConnection, Action<string>? onWarning)
    {
        var sslMode = new NpgsqlConnectionStringBuilder(keyValueConnectionString).SslMode;

        // ALLOW-LIST (fail closed): only certificate-validated TLS is silent, and Require
        // warns; EVERYTHING else falls to `default` — Disable/Allow/Prefer AND any undefined
        // SslMode value — because none of them guarantees an encrypted connection.
        switch (sslMode)
        {
            case SslMode.VerifyCA:
            case SslMode.VerifyFull:
                return;

            case SslMode.Require:
                onWarning?.Invoke(
                    "Production database connection uses sslmode=Require, which encrypts the connection " +
                    "but does not verify the server certificate. Prefer sslmode=VerifyFull with a host CA " +
                    "to defend against MITM.");
                return;

            default:
                if (allowInsecureConnection)
                {
                    onWarning?.Invoke(
                        "INSECURE database connection explicitly permitted via " +
                        $"Database:AllowInsecureConnection (sslmode='{sslMode}'): database traffic is " +
                        "UNENCRYPTED (plaintext/MITM risk). A real deployment must use sslmode=Require at " +
                        "minimum (sslmode=VerifyFull with a host CA preferred) and never set this flag.");
                    return;
                }

                throw new InvalidOperationException(
                    $"Production database connections must use TLS: sslmode='{sslMode}' does not guarantee " +
                    "an encrypted connection (plaintext/MITM risk). Set sslmode=Require at minimum " +
                    "(sslmode=VerifyFull with a host CA is strongly preferred), or set " +
                    "Database:AllowInsecureConnection=true to explicitly permit an unencrypted connection.");
        }
    }
}
