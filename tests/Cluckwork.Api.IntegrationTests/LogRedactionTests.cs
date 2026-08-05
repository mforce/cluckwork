namespace Cluckwork.Api.IntegrationTests;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using Cluckwork.Api.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;

// #273 — SensitiveDataRedactionEnricher, exercised directly against a tiny
// throwaway Serilog pipeline (no HTTP, no Postgres): fast and deterministic,
// and precise about exactly what the enricher does to a given property. The
// end-to-end path (a real caller-controlled report through the actual
// /client-errors endpoint) is covered separately by
// ClientErrorReportTests.Report_content_containing_an_email_and_connection_credentials_is_redacted_before_it_reaches_the_log
// — that one proves the enricher is actually WIRED IN; this file proves what
// it does once it runs. Every secret-shaped value here is generated at
// runtime, never a literal (GitGuardian flags a literal secret regardless of
// which test file it sits in).
public sealed class LogRedactionTests
{
    private static (ILogger Logger, ConcurrentQueue<LogEvent> Events) BuildLogger()
    {
        var events = new ConcurrentQueue<LogEvent>();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.With(new SensitiveDataRedactionEnricher())
            .WriteTo.Sink(new CollectingSink(events))
            .CreateLogger();
        return (logger, events);
    }

    private static string? ScalarOf(LogEvent e, string name) =>
        e.Properties.TryGetValue(name, out var value) && value is ScalarValue scalar
            ? scalar.Value?.ToString()
            : null;

    [Fact]
    public void A_property_named_Password_is_fully_redacted_regardless_of_content()
    {
        var (logger, events) = BuildLogger();
        var secret = $"Sw0rdfish-{Guid.NewGuid():N}";

        logger.Information("Login attempt with {Password}", secret);

        var e = Assert.Single(events);
        Assert.Equal("[REDACTED]", ScalarOf(e, "Password"));
        Assert.DoesNotContain(secret, e.RenderMessage());
    }

    [Theory]
    [InlineData("CurrentPassword")]
    [InlineData("NewPassword")]
    [InlineData("RefreshToken")]
    [InlineData("AccessToken")]
    [InlineData("ConnectionString")]
    [InlineData("Cookie")]
    public void Every_forbidden_field_name_is_redacted_case_insensitively(string fieldName)
    {
        var (logger, events) = BuildLogger();
        var secret = $"secret-{Guid.NewGuid():N}";

        // Deliberately mixed case — the match must be case-insensitive.
        var mixedCaseTemplate = $"Value {{{fieldName.ToUpperInvariant()}}}";
        logger.Information(mixedCaseTemplate, secret);

        var e = Assert.Single(events);
        Assert.DoesNotContain(secret, e.RenderMessage());
    }

    [Fact]
    public void An_email_embedded_in_free_text_is_redacted_but_the_rest_of_the_text_survives()
    {
        var (logger, events) = BuildLogger();
        var email = $"{Guid.NewGuid():N}@example.test";

        logger.Information("Client error at {Route}: contact {Message}", "/orders", $"please reach {email} for help");

        var e = Assert.Single(events);
        var rendered = e.RenderMessage();
        Assert.DoesNotContain(email, rendered);
        Assert.Contains("[REDACTED]", rendered);
        Assert.Contains("please reach", rendered);
        Assert.Contains("for help", rendered);
    }

    [Fact]
    public void A_bearer_token_embedded_in_free_text_is_redacted()
    {
        var (logger, events) = BuildLogger();
        var token = Convert.ToBase64String(Guid.NewGuid().ToByteArray()).TrimEnd('=');

        logger.Information("Rejected header {Message}", $"Authorization: Bearer {token}");

        var e = Assert.Single(events);
        var rendered = e.RenderMessage();
        Assert.DoesNotContain(token, rendered);
        Assert.Contains("[REDACTED]", rendered);
    }

    // #273 codex review (round 3) — this repo's own refresh token
    // (IdentityProvider.GenerateRefreshToken) is STANDARD, not URL-safe, Base64
    // (Convert.ToBase64String), so it can contain '+' and '/'. A pattern
    // missing either character class doesn't just fail to match — it redacts
    // only up to that character and leaks the rest. 32 random bytes almost
    // certainly contain both across enough attempts; loop deterministically
    // instead of relying on one draw.
    [Fact]
    public void A_bearer_token_containing_plus_and_slash_is_redacted_in_full()
    {
        var (logger, events) = BuildLogger();
        string token;
        do
        {
            token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        } while (!token.Contains('+') || !token.Contains('/'));

        logger.Information("Rejected header {Message}", $"Authorization: Bearer {token}");

        var e = Assert.Single(events);
        var rendered = e.RenderMessage();
        Assert.DoesNotContain(token, rendered);
        Assert.Contains("[REDACTED]", rendered);
        // The bug this pins: a class missing '+'/'/' still matches "Bearer
        // <prefix>" up to the first one and stops, leaving that character and
        // everything after it — the TAIL — as unredacted literal text. The
        // full-token check above passes even then (the contiguous string is
        // broken by the partial redaction), so assert the tail specifically.
        var tail = token[token.IndexOfAny(['+', '/'])..];
        Assert.DoesNotContain(tail, rendered);
    }

    [Fact]
    public void A_jwt_shaped_value_embedded_in_free_text_is_redacted()
    {
        var (logger, events) = BuildLogger();
        // Not a real signed JWT — three base64url-shaped segments with the
        // "eyJ" header prefix every real JWT has (base64url of `{"...`), which
        // is exactly what the pattern requires and what a real captured-token
        // leak looks like.
        var fakeJwt = $"eyJ{Guid.NewGuid():N}.{Guid.NewGuid():N}.{Guid.NewGuid():N}";

        logger.Information("Captured value: {Message}", fakeJwt);

        var e = Assert.Single(events);
        Assert.DoesNotContain(fakeJwt, e.RenderMessage());
    }

    // #273 — the false positive this pattern's "eyJ" requirement exists to
    // prevent: three real, ordinary dot-separated namespace segments (this
    // exact string briefly corrupted Serilog's own {SourceContext} property
    // via an earlier, looser version of the pattern and silently broke
    // HandlerLoggingTests — a genuine regression this test pins against
    // recurring, not a hypothetical).
    [Fact]
    public void A_three_segment_dotted_namespace_is_not_mistaken_for_a_jwt()
    {
        var (logger, events) = BuildLogger();
        const string sourceContextShaped = "DailyEntries.SubmitDailyEntry.SubmitDailyEntryHandler";

        logger.Information("Handler {Message} ran", sourceContextShaped);

        var e = Assert.Single(events);
        Assert.Equal(sourceContextShaped, ScalarOf(e, "Message"));
    }

    [Fact]
    public void Connection_string_credentials_embedded_in_free_text_are_redacted()
    {
        var (logger, events) = BuildLogger();
        var password = Guid.NewGuid().ToString("N");

        logger.Information("Config dump: {Message}", $"Host=db;Username=app;Password={password};");

        var e = Assert.Single(events);
        var rendered = e.RenderMessage();
        Assert.DoesNotContain(password, rendered);
        Assert.Contains("Host=db", rendered); // non-secret parts of the string are untouched
    }

    // #273 codex review (P1a) — a QUOTED connection-string credential used to
    // fail the pattern entirely (the bare-value alternative stops at the
    // first quote character) and reach the sink whole. ADO.NET connection
    // strings legitimately quote a value containing a space or a semicolon,
    // so this is a real shape, not a contrived one. Covers double- and
    // single-quoted forms, an embedded `;` inside the quotes, and `Pwd=` as
    // well as `Password=`.
    [Theory]
    [InlineData("Host=db;Password=\"{0}\";")]
    [InlineData("Host=db;Password='{0}';")]
    [InlineData("Host=db;Pwd=\"{0}\";")]
    public void A_quoted_connection_string_password_containing_a_semicolon_is_fully_redacted(string template)
    {
        var (logger, events) = BuildLogger();
        // The quoted content itself contains a space and a semicolon — exactly
        // the shape that requires quoting in a real connection string, and the
        // shape the bare-value pattern alone cannot terminate on.
        var secret = $"se mi;colon-{Guid.NewGuid():N}";

        logger.Information("Config dump: {Message}", string.Format(template, secret));

        var e = Assert.Single(events);
        var rendered = e.RenderMessage();
        Assert.DoesNotContain(secret, rendered);
        Assert.DoesNotContain(";colon", rendered); // the embedded `;` must not leak either
        Assert.Contains("Host=db", rendered);
        Assert.Contains("[REDACTED]", rendered);
    }

    // #273 codex review (P1a) — the bare (unquoted) form must still work exactly
    // as before; this pins the pre-existing behavior alongside the new quoted
    // coverage above so a future edit can't fix one shape by breaking the other.
    [Fact]
    public void A_bare_unquoted_connection_string_password_is_still_fully_redacted()
    {
        var (logger, events) = BuildLogger();
        var secret = $"plainsecret-{Guid.NewGuid():N}";

        logger.Information("Config dump: {Message}", $"Host=db;Password={secret};Pooling=true");

        var e = Assert.Single(events);
        var rendered = e.RenderMessage();
        Assert.DoesNotContain(secret, rendered);
        Assert.Contains("Host=db", rendered);
        Assert.Contains("Pooling=true", rendered); // text after the credential survives
    }

    // #273 codex review (P1a) — false-positive guard for the widened pattern: a
    // GUID and a dotted namespace never contain a "password=" / "pwd=" prefix,
    // so the new quoted alternatives must not touch them either.
    [Fact]
    public void A_guid_and_a_dotted_namespace_are_untouched_by_the_widened_connection_string_pattern()
    {
        var (logger, events) = BuildLogger();
        var id = Guid.NewGuid();
        const string ns = "DailyEntries.SubmitDailyEntry.SubmitDailyEntryHandler";

        logger.Information("Handler {Message} for account {AccountId}", ns, id);

        var e = Assert.Single(events);
        Assert.Equal(id.ToString(), ScalarOf(e, "AccountId"));
        var rendered = e.RenderMessage();
        Assert.Contains(ns, rendered);
    }

    // #273 codex review (round 2, P1a) — the regression in round 1's OWN fix.
    // ADO.NET escapes a quote inside a quoted value by DOUBLING it, and round
    // 1's `"[^"]*"` alternative terminates on the first quote of that pair, so
    // `Password="alpha""omega;tail"` was redacted only as far as `"alpha"` and
    // emitted `"omega;tail"` — the rest of the credential — in cleartext.
    // Asserts the WHOLE value is gone with nothing trailing, for both quote
    // styles and both key spellings.
    [Theory]
    [InlineData("Host=db;Password=\"{0}\";", '"')]
    [InlineData("Host=db;Password='{0}';", '\'')]
    [InlineData("Host=db;Pwd=\"{0}\";", '"')]
    [InlineData("Host=db;Pwd='{0}';", '\'')]
    public void A_doubled_quote_inside_a_quoted_credential_does_not_end_the_redaction(
        string template, char quote)
    {
        var (logger, events) = BuildLogger();
        // The doubled quote is the escape ADO.NET uses for a literal quote
        // INSIDE the value; the `;` after it is what leaked in round 1.
        var head = $"alpha-{Guid.NewGuid():N}";
        var tail = $"omega;tail-{Guid.NewGuid():N}";
        var secret = $"{head}{quote}{quote}{tail}";

        logger.Information("Config dump: {Message}", string.Format(template, secret));

        var e = Assert.Single(events);
        // Asserted on the property value itself, not RenderMessage(), so the
        // "nothing trails the redaction" check isn't confused by the quotes
        // Serilog puts around a rendered string scalar.
        var message = ScalarOf(e, "Message");
        Assert.NotNull(message);
        Assert.DoesNotContain(head, message);
        Assert.DoesNotContain(tail, message);   // the leak: everything after the doubled quote
        Assert.DoesNotContain("omega", message);
        Assert.StartsWith("Host=db;", message, StringComparison.Ordinal);
        // Nothing of the credential span survives: what follows the redaction
        // marker is the closing `;` of the connection string, not a quote.
        Assert.EndsWith("=[REDACTED];", message, StringComparison.Ordinal);
    }

    // #273 codex review (round 2, P1a) — an UNTERMINATED quoted value (a
    // truncated dump, or caller-controlled free text engineered to look like
    // one) must fail CLOSED: redact to end of string rather than let the regex
    // simply not match and pass the tail through.
    [Fact]
    public void An_unterminated_quoted_credential_is_redacted_to_the_end_of_the_string()
    {
        var (logger, events) = BuildLogger();
        var secret = $"truncated-{Guid.NewGuid():N}";

        logger.Information("Config dump: {Message}", $"Host=db;Password=\"{secret}");

        var e = Assert.Single(events);
        var rendered = e.RenderMessage();
        Assert.DoesNotContain(secret, rendered);
        Assert.Contains("Host=db", rendered);
    }

    [Fact]
    public void Libpq_uri_credentials_embedded_in_free_text_are_redacted()
    {
        var (logger, events) = BuildLogger();
        var password = Guid.NewGuid().ToString("N");

        logger.Information("Config dump: {Message}", $"postgresql://appuser:{password}@db.internal:5432/cluckwork");

        var e = Assert.Single(events);
        var rendered = e.RenderMessage();
        Assert.DoesNotContain(password, rendered);
        Assert.DoesNotContain("appuser", rendered); // the whole userinfo segment is dropped, not just the password
        Assert.Contains("db.internal", rendered); // host is not a credential — stays
    }

    // #273 — the regression this class exists to prevent: an EARLIER,
    // all-optional-separator version of the phone-number pattern matched a
    // 10-consecutive-digit run inside an ordinary GUID (GUIDs are ~62% digit
    // characters by alphabet share, so a 10-digit run is common, not rare),
    // which would have silently corrupted RequestLoggingTests'
    // Authenticated_request_completion_carries_the_account_id assertion the
    // moment a GUID happened to contain one. Proven here directly, with many
    // random GUIDs rather than one, since the failure is probabilistic.
    [Fact]
    public void Guid_valued_properties_are_never_altered_by_the_phone_number_pattern()
    {
        var (logger, events) = BuildLogger();

        for (var i = 0; i < 200; i++)
        {
            var id = Guid.NewGuid();
            logger.Information("Account {AccountId} resolved", id);
            var e = events.Last();
            Assert.Equal(id.ToString(), ScalarOf(e, "AccountId"));
        }
    }

    [Fact]
    public void A_genuine_looking_phone_number_in_free_text_is_redacted()
    {
        var (logger, events) = BuildLogger();

        logger.Information("Contact on file: {Message}", "call me at 555-201-4832 please");

        var e = Assert.Single(events);
        var rendered = e.RenderMessage();
        Assert.DoesNotContain("555-201-4832", rendered);
        Assert.Contains("[REDACTED]", rendered);
        Assert.Contains("call me at", rendered);
    }

    [Fact]
    public void A_field_explicitly_named_Phone_or_Address_is_fully_redacted()
    {
        var (logger, events) = BuildLogger();

        logger.Information("Contact updated: {Phone} {Address}", "not-even-digits", "123 Nowhere Ln");

        var e = Assert.Single(events);
        var rendered = e.RenderMessage();
        Assert.DoesNotContain("Nowhere", rendered);
        Assert.DoesNotContain("not-even-digits", rendered);
    }

    private sealed class CollectingSink(ConcurrentQueue<LogEvent> events) : ILogEventSink
    {
        public void Emit(LogEvent logEvent) => events.Enqueue(logEvent);
    }
}
