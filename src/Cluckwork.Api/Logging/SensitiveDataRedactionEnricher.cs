namespace Cluckwork.Api.Logging;

using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;

// #273 — redact credentials, tokens, cookies, connection strings, and emails
// BEFORE any log line leaves the process, regardless of sink: this enricher is
// wired into the Serilog pipeline itself (CluckworkTelemetryServiceCollectionExtensions
// .Enrich.With(...)), so it applies to every event on every sink this host has —
// console today, and any future log-aggregation sink joins the same pipeline via
// ReadFrom.Services rather than needing its own copy of this logic.
//
// Applied to EVERY log event on the host, not just the auth/security events in
// Cluckwork.Application.Common.SecurityEvents — that is deliberate. The concrete
// leak #273 was filed against is caller-controlled FREE TEXT (ClientErrorEndpoints'
// Message/Route/Stack, #217): a field name like "Message" gives no hint about what
// it contains, so a name-based allowlist alone would miss it. Two layers:
//
//  - STRUCTURAL: a property whose NAME matches a forbidden field (password,
//    token, connection string, ...) is replaced OUTRIGHT, regardless of type or
//    content — the name alone is enough signal, and a partially-redacted secret
//    is often still exploitable (a truncated password is still most of one).
//  - CONTENT: every string-valued property (any name) is scanned for
//    recognizable patterns (email, bearer/JWT-shaped token, connection-string
//    credentials) and matches are replaced in place — this is what protects
//    free text, where the field name gives no hint.
//
// Best-effort, NOT a guarantee for every possible PII shape: regex cannot
// reliably find an arbitrary street address, and a value baked directly into a
// message via C# string interpolation (rather than a Serilog/ILogger template
// hole) has no PROPERTY for this enricher to touch — Serilog exposes no API to
// rewrite a LogEvent's already-rendered template text. That residual gap is why
// structured logging (named template holes, never raw interpolation of a
// sensitive value into the message string) stays the primary control; see
// docs/security/log-redaction-policy.md for the full policy and the field
// contract each stable security event carries.
public sealed class SensitiveDataRedactionEnricher : ILogEventEnricher
{
    private const string Redacted = "[REDACTED]";

    // Case-insensitive EXACT property-name match. Whole value is dropped
    // regardless of type or content.
    private static readonly HashSet<string> ForbiddenPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Password", "CurrentPassword", "NewPassword", "ConfirmPassword", "TemporaryPassword",
        "Pwd", "Secret", "ClientSecret", "ApiKey",
        "Token", "AccessToken", "RefreshToken", "StepUpToken", "IdToken",
        "PrivateKey", "PrivateKeyPem",
        "Authorization", "Cookie", "SetCookie", "ConnectionString",
        "Phone", "PhoneNumber", "MobileNumber", "Address", "StreetAddress", "HomeAddress",
    };

    private static readonly Regex EmailPattern = new(
        @"[A-Za-z0-9.+_-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Three dot-separated base64url segments — a compact JWT (access token,
    // refresh-token-shaped value, step-up grant, ...). The header segment MUST
    // start with "eyJ": every standard JWT header is base64url of a JSON
    // object starting with `{"` (e.g. {"alg":"RS256",...}), which is ALWAYS
    // "eyJ..." under base64 — a well-established signature real secret
    // scanners rely on for exactly this reason. Required, not optional: an
    // earlier version of this pattern matched ANY three 10+-char dot-separated
    // segments and corrupted this very host's own Serilog {SourceContext}
    // property — "DailyEntries.SubmitDailyEntry.SubmitDailyEntryHandler" is
    // three real C# namespace segments that innocently satisfy a length-only
    // pattern (caught by this enricher's own test suite failing a PRE-EXISTING
    // handler-logging test, not a hypothetical). Requiring the JWT-specific
    // prefix keeps ordinary namespaced identifiers out of scope entirely.
    private static readonly Regex JwtPattern = new(
        @"\beyJ[A-Za-z0-9_-]{7,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BearerPattern = new(
        @"\bBearer\s+[A-Za-z0-9\-_.=]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // key=value credential pairs inside an ADO.NET-style connection string
    // (Password=..., Pwd=...).
    private static readonly Regex ConnectionStringCredentialPattern = new(
        @"\b(password|pwd)\s*=\s*[^;""'\s]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // Userinfo credentials in a libpq/generic URI (scheme://user:pass@host).
    private static readonly Regex UriCredentialPattern = new(
        @"[A-Za-z][A-Za-z0-9+.-]*://[^/@\s:]+:[^/@\s]+@",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Conservative phone-number heuristic: MANDATORY separators between the
    // 3-3-4 digit groups, and neither edge may sit against a word character or
    // hyphen. Both constraints exist to keep this from firing on a GUID or any
    // other hyphen/hex identifier that happens to contain a 10-digit run (a
    // real false positive this enricher's own test suite guards against, next
    // to RequestLoggingTests' AccountId assertions) — an all-optional-separator
    // version of this pattern matched inside ordinary GUIDs often enough to be
    // a real hazard, not a theoretical one.
    private static readonly Regex PhonePattern = new(
        @"(?<![\w-])(?:\+\d{1,3}[-.\s])?\(?\d{3}\)?[-.\s]\d{3}[-.\s]\d{4}(?![\w-])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        // Snapshot the names — the loop mutates logEvent.Properties via
        // AddOrUpdateProperty, which would otherwise invalidate enumeration.
        foreach (var name in logEvent.Properties.Keys.ToArray())
        {
            var value = logEvent.Properties[name];
            var redacted = ForbiddenPropertyNames.Contains(name)
                ? new ScalarValue(Redacted)
                : RedactValue(value);
            if (!ReferenceEquals(redacted, value))
                logEvent.AddOrUpdateProperty(new LogEventProperty(name, redacted));
        }
    }

    private static LogEventPropertyValue RedactValue(LogEventPropertyValue value)
    {
        switch (value)
        {
            case ScalarValue { Value: string text }:
                var redactedText = RedactContent(text);
                return redactedText == text ? value : new ScalarValue(redactedText);
            case SequenceValue sequence:
                return new SequenceValue(sequence.Elements.Select(RedactValue));
            case StructureValue structure:
                return new StructureValue(
                    structure.Properties.Select(p => ForbiddenPropertyNames.Contains(p.Name)
                        ? new LogEventProperty(p.Name, new ScalarValue(Redacted))
                        : new LogEventProperty(p.Name, RedactValue(p.Value))),
                    structure.TypeTag);
            case DictionaryValue dictionary:
                return new DictionaryValue(dictionary.Elements.Select(kv =>
                {
                    var key = kv.Key.Value?.ToString();
                    var redactedElement = key is not null && ForbiddenPropertyNames.Contains(key)
                        ? new ScalarValue(Redacted)
                        : RedactValue(kv.Value);
                    return new KeyValuePair<ScalarValue, LogEventPropertyValue>(kv.Key, redactedElement);
                }));
            default:
                return value;
        }
    }

    // Order matters where patterns could overlap: Bearer runs before the bare
    // JWT pattern so a "Bearer <jwt>" header value is consumed as one match —
    // the later JWT pass then has nothing left inside it to double-redact.
    private static string RedactContent(string value)
    {
        var result = value;
        result = BearerPattern.Replace(result, Redacted);
        result = JwtPattern.Replace(result, Redacted);
        result = UriCredentialPattern.Replace(
            result,
            m => m.Value[..(m.Value.IndexOf("://", StringComparison.Ordinal) + 3)] + Redacted + "@");
        result = ConnectionStringCredentialPattern.Replace(result, m => $"{m.Groups[1].Value}={Redacted}");
        result = EmailPattern.Replace(result, Redacted);
        result = PhonePattern.Replace(result, Redacted);
        return result;
    }
}
