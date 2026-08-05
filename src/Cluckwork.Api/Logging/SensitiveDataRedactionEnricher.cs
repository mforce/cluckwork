namespace Cluckwork.Api.Logging;

using System.Text;
using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;

// #273 — redact credentials, tokens, cookies, connection strings, and emails
// BEFORE any log line leaves the process, regardless of sink: RedactingLoggerPipeline
// wires this enricher in front of every sink this host has — console today, and any
// future log-aggregation sink, whether it arrives through `Serilog:WriteTo` or
// through ReadFrom.Services, rather than needing its own copy of this logic.
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
// An enricher only ever sees PROPERTIES. Two things are therefore out of its
// reach, and are handled (or explicitly not handled) elsewhere:
//
//  - LogEvent.Exception — get-only, rendered by Serilog separately from the
//    properties, and not replaceable by any enricher or filter. Covered by
//    ExceptionRedactingSink, which runs RedactText over an exception's rendered
//    text and substitutes the event; RedactingLoggerPipeline is what guarantees
//    that wrapper sits in front of every sink.
//  - A value baked into the message via C# string INTERPOLATION rather than a
//    template hole. That leaves no property to touch and Serilog exposes no API
//    to rewrite already-rendered template text, so it remains a genuine residual
//    gap — which is why structured logging (named template holes, never raw
//    interpolation of a sensitive value) stays the primary control.
//
// Content matching is also best-effort by nature: regex cannot reliably find an
// arbitrary street address. See docs/security/log-redaction-policy.md for the
// full policy, the exact coverage boundary, and the field contract each stable
// security event carries.
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

    // RFC 6750 §2.1's b64token alphabet (ALPHA / DIGIT / "-" / "." / "_" / "~" /
    // "+" / "/", padded with "="). `+` and `/` matter concretely here: this
    // repo's own refresh token (IdentityProvider.GenerateRefreshToken) is
    // standard — not URL-safe — Base64, so it can contain either. Missing them
    // from the class doesn't just miss a match; a value STARTING with one is
    // skipped entirely, and one containing one mid-string is redacted only up
    // to that character, leaking the remainder (codex review of #349).
    private static readonly Regex BearerPattern = new(
        @"\bBearer\s+[A-Za-z0-9\-._~+/=]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // The KEY half of an ADO.NET-style connection-string credential pair
    // (Password=..., Pwd=...). Only the key — where the VALUE ends is decided
    // by RedactConnectionStringCredentials below, in code, not by this regex.
    //
    // #273 codex review (round 2, P1a) — round 1 widened a single regex to
    // `(?:"[^"]*"|'[^']*'|[^;"'\s]+)` so a QUOTED value (a real shape: ADO.NET
    // requires quoting a value containing `;` or a space) stopped leaking. That
    // fix was itself incomplete: ADO.NET escapes a quote INSIDE a quoted value
    // by DOUBLING it, and `[^"]*` terminates on the first quote of that pair —
    // so `Password="alpha""omega;tail"` redacted only `"alpha"` and emitted
    // `"omega;tail"`, i.e. the rest of the credential, in cleartext.
    //
    // Deliberately NOT solved by a cleverer regex. The canonical doubled-quote
    // idioms — `(?:[^"]|"")*` and its unrolled form `[^"]*(?:""[^"]*)*` — put a
    // quantifier inside a quantifier, and while both happen to be unambiguous,
    // reasoning about their backtracking is exactly the kind of judgement call
    // that must not sit on the path of caller-controlled free text from the
    // ANONYMOUS /client-errors endpoint (#217): a wrong call there is a CPU DoS,
    // not a cosmetic bug. Scanning the value in code is provably linear (one
    // forward pass, a cursor that never moves backwards) and additionally lets
    // the scanner FAIL CLOSED on a malformed input a regex would simply not
    // match — an unterminated quoted value redacts to end-of-string rather than
    // leaking its tail.
    private static readonly Regex ConnectionStringCredentialKeyPattern = new(
        @"\b(password|pwd)\s*=\s*",
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

    // The same content-pattern pass the enricher applies to every string-valued
    // property, exposed for the ONE thing an ILogEventEnricher structurally
    // cannot reach: LogEvent.Exception (get-only, and Serilog renders it
    // separately from the properties). ExceptionRedactingSink runs an
    // exception's rendered text through this before any sink sees the event —
    // see that class and docs/security/log-redaction-policy.md for the coverage
    // boundary.
    public static string RedactText(string value) => RedactContent(value);

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
        result = RedactConnectionStringCredentials(result);
        result = EmailPattern.Replace(result, Redacted);
        result = PhonePattern.Replace(result, Redacted);
        return result;
    }

    // Single forward pass: find the next `password=` / `pwd=` key, emit the text
    // before it, emit `<key>=[REDACTED]`, then skip the whole value using
    // ADO.NET's own quoting rules (EndOfCredentialValue) and resume the search
    // from there. The cursor only ever moves forwards and every character is
    // examined at most twice, so this is O(n) in the input length no matter how
    // hostile the input — the property the previous all-regex version could not
    // be given without nesting quantifiers.
    private static string RedactConnectionStringCredentials(string value)
    {
        var match = ConnectionStringCredentialKeyPattern.Match(value);
        if (!match.Success) return value;

        var builder = new StringBuilder(value.Length);
        var cursor = 0;
        while (match.Success)
        {
            builder.Append(value, cursor, match.Index - cursor);
            builder.Append(match.Groups[1].Value).Append('=').Append(Redacted);
            cursor = EndOfCredentialValue(value, match.Index + match.Length);
            if (cursor >= value.Length) break;
            match = ConnectionStringCredentialKeyPattern.Match(value, cursor);
        }

        builder.Append(value, cursor, value.Length - cursor);
        return builder.ToString();
    }

    // Index just past the credential value starting at `start`, per ADO.NET
    // connection-string quoting:
    //  - a value opening with " or ' runs to the matching close quote, and a
    //    DOUBLED quote inside it is a literal quote, NOT the terminator (the
    //    round-1 regex's blind spot);
    //  - an unterminated quoted value consumes the rest of the string — fail
    //    CLOSED, since over-redacting a malformed dump beats emitting the tail
    //    of a credential;
    //  - a bare value ends at the first `;`, quote, or whitespace, exactly as
    //    the previous bare-value character class did.
    private static int EndOfCredentialValue(string value, int start)
    {
        if (start >= value.Length) return start;

        var quote = value[start];
        if (quote is not ('"' or '\''))
        {
            var bare = start;
            while (bare < value.Length
                   && value[bare] != ';'
                   && value[bare] != '"'
                   && value[bare] != '\''
                   && !char.IsWhiteSpace(value[bare]))
                bare++;
            return bare;
        }

        var quoted = start + 1;
        while (quoted < value.Length)
        {
            if (value[quoted] != quote) { quoted++; continue; }
            if (quoted + 1 < value.Length && value[quoted + 1] == quote) { quoted += 2; continue; }
            return quoted + 1;
        }

        return value.Length;
    }
}
