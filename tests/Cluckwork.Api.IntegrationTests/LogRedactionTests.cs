namespace Cluckwork.Api.IntegrationTests;

using System.Collections.Concurrent;
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
