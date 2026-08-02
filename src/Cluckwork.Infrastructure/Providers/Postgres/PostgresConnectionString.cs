namespace Cluckwork.Infrastructure.Providers.Postgres;

using System.Data.Common;
using Npgsql;

// Normalizes a Postgres connection string and enforces the production TLS floor.
// Call this ONCE at startup (composition root); the result is a plain Npgsql key-value
// string safe to hand to UseNpgsql on every DbContext resolution.
//
// #261 — accepts libpq/managed URI form (postgres://, postgresql://) in addition to
//        Npgsql key-value; Npgsql's own parser only understands key-value and throws on a
//        URI. Query params with no Npgsql equivalent (sslcompression, …) are
//        skipped-with-warning rather than failing the whole connection. BEWARE: a param
//        being absent from ContainsKey does NOT mean Npgsql lacks an equivalent — it
//        usually means the equivalent is spelled differently (libpq "keepalives" is
//        Npgsql "Tcp Keepalive"). Look for the differently-spelled keyword before
//        concluding a param is unmappable; that assumption is what shipped #332.
// #262 — in Production, enforces the TLS floor as an ALLOW-LIST (fail closed): only
//        VerifyCA/VerifyFull pass silently and Require passes with a warning; EVERY other
//        value — Disable/Allow/Prefer AND any undefined SslMode (e.g. (SslMode)99 from a
//        raw `SSL Mode=99`) — throws, unless Database:AllowInsecureConnection is explicitly
//        set, in which case it boots with a loud warning. It never auto-injects or upgrades.
// #332 — disables GSSAPI/Kerberos encryption negotiation unless the operator asked for it.
//        Npgsql's GssEncryptionMode defaults to Prefer, so every connector probes the GSS
//        stack before authenticating; on a runtime image without libgssapi-krb5-2 (#267
//        keeps the image minimal) the .NET native security shim prints two UNSTRUCTURED
//        lines to stderr — outside Serilog, so they can't be filtered or shipped as
//        structured events — that read like a connection failure on every deploy.
//        Verified by loader trace: with GssEncryptionMode=Prefer the process dlopens
//        libgssapi_krb5.so.2 / libkrb5.so.3 even against a scram-sha-256 server; with
//        Disable there is zero gssapi/krb5 loader activity and the connection is
//        otherwise identical. The #262 "never auto-injects" rule above is scoped to
//        sslmode, which this leaves strictly alone.
public static class PostgresConnectionString
{
    // Both spellings are valid libpq URI schemes (PostgreSQL docs §34.1.1.2).
    private static readonly string[] UriSchemes = ["postgres", "postgresql"];

    private const int DefaultPort = 5432;

    // Npgsql's canonical spelling. It accepts any casing of this and of the spaceless
    // "gssencryptionmode"; underscores and the libpq name "gssencmode" are NOT accepted.
    // CollapseKeyword below covers that set.
    private const string GssEncryptionModeKeyword = "GSS Encryption Mode";

    // libpq/managed URI query params whose Npgsql keyword differs (or which Npgsql only
    // accepts under a spaced name — e.g. "channel_binding" throws, "Channel Binding" works).
    // Npgsql 10.0.3 DOES support all of these under the spaced name, so they must be MAPPED,
    // not dropped: channel_binding=require is an explicit SCRAM anti-MITM control an operator
    // set. sslmode / ssl / cert params are handled explicitly below, not through this map.
    private static readonly Dictionary<string, string> LibpqParameterToNpgsqlKeyword =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["sslrootcert"] = "Root Certificate",
            ["sslcert"] = "SSL Certificate",
            ["sslkey"] = "SSL Key",
            ["dbname"] = "Database",
            ["application_name"] = "Application Name",
            ["connect_timeout"] = "Timeout",
            ["channel_binding"] = "Channel Binding",
            ["target_session_attrs"] = "Target Session Attributes",
            ["ssl_negotiation"] = "SSL Negotiation",
            // #332 — Npgsql does NOT recognize the libpq spelling "gssencmode" under any
            // casing, so without this entry it fell through to the unknown-parameter branch
            // and was dropped with a warning: an operator could not control GSS negotiation
            // from a connection URI at all. The libpq values (disable/prefer/require) are
            // exactly the GssEncryptionMode member names, so no value translation is needed.
            ["gssencmode"] = GssEncryptionModeKeyword,
        };

    /// <summary>
    /// Returns a key-value connection string (translating URI form when needed) and, in
    /// Production, enforces the TLS floor. Throws <see cref="InvalidOperationException"/> for
    /// a mode that does not guarantee TLS (unless <paramref name="allowInsecureConnection"/>
    /// is set, which downgrades that to a loud warning); invokes <paramref name="onWarning"/>
    /// for a Require-only mode and for skipped URI parameters. Also defaults
    /// GSS encryption negotiation to off when the caller did not specify it (#332).
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

        // AFTER the floor, so the TLS decision is made against exactly what the operator
        // supplied. Defense-in-depth rather than load-bearing: EnforceTlsFloor reads only
        // .SslMode, so appending a different keyword cannot change its verdict today.
        return ApplyGssEncryptionDefault(normalized);
    }

    // #332 — Cluckwork authenticates with a password, never Kerberos, so GSS *encryption*
    // negotiation is dead weight that only produces pre-logger stderr noise on an image
    // without libgssapi-krb5-2. Turn it off by default.
    //
    // This does NOT weaken anything: GssEncryptionMode governs the optional GSSAPI
    // transport wrapper, which is orthogonal to SSL Mode (the TLS floor above is
    // untouched) and to GSS *authentication* (a separate Npgsql code path, reached only
    // if the server actually issues an AuthenticationGSS challenge).
    //
    // Keyed on PRESENCE, not value: 'prefer' is Npgsql's own default, so comparing
    // against the enum default would silently override an operator who asked for it.
    // A Kerberos-fronted deployment sets gssencmode explicitly and keeps it.
    private static string ApplyGssEncryptionDefault(string keyValueConnectionString)
    {
        if (SpecifiesGssEncryptionMode(keyValueConnectionString))
        {
            return keyValueConnectionString;
        }

        // Append textually rather than round-tripping through NpgsqlConnectionStringBuilder:
        // a rebuild would reorder and requote the operator's own string, and would throw on
        // any keyword this Npgsql version doesn't know. The TrimEnd is COSMETIC — Npgsql
        // parses "…;;GSS Encryption Mode=Disable" identically — so it is pinned by asserting
        // the resulting TEXT, not by reparsing it.
        return string.Concat(
            keyValueConnectionString.TrimEnd(';', ' '),
            ";", GssEncryptionModeKeyword, "=", nameof(GssEncryptionMode.Disable));
    }

    private static bool SpecifiesGssEncryptionMode(string keyValueConnectionString)
    {
        // The BASE builder, deliberately: NpgsqlConnectionStringBuilder.ContainsKey reports
        // true for every keyword it KNOWS (defaults included), so it cannot distinguish
        // "the operator set this" from "Npgsql has a default for this". DbConnectionStringBuilder
        // exposes only the keys actually present in the text.
        var supplied = new DbConnectionStringBuilder { ConnectionString = keyValueConnectionString };

        foreach (string key in supplied.Keys)
        {
            if (CollapseKeyword(key).Equals(
                    CollapseKeyword(GssEncryptionModeKeyword), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // Npgsql accepts any casing of "gssencryptionmode" and of "gss encryption mode" (and
    // has NO registered synonyms for this property), so both must count as operator-supplied.
    // Collapsing spaces covers that set. It deliberately over-matches a few spellings Npgsql
    // itself rejects (doubled spaces, tabs): those throw at parse time either way, so
    // over-matching costs nothing, while under-matching would silently override an operator.
    private static string CollapseKeyword(string keyword) =>
        keyword.Replace(" ", string.Empty);

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
            if (builder.ContainsKey(keyword))
            {
                // A keyword Npgsql knows (native, or mapped from its libpq spelling):
                // assign OUTSIDE any catch, so an INVALID VALUE on a real setting
                // (e.g. connect_timeout=garbage) surfaces and fails startup instead of
                // being silently dropped as if the parameter were unknown.
                builder[keyword] = value;
            }
            else
            {
                // No Npgsql equivalent under ANY spelling (sslcompression, …). Skip it
                // rather than fail the whole connection — Npgsql negotiates fine without it.
                // NOTE: keepalives*/client_encoding also land here today, but only because
                // they are not in the map yet — Npgsql DOES support them ("Tcp Keepalive",
                // "Client Encoding"). Reaching this branch is not proof a param is
                // unsupported; see the header comment.
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
