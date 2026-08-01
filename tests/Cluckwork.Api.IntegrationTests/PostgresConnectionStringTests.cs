namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Infrastructure.Providers;
using Cluckwork.Infrastructure.Providers.Postgres;
using Microsoft.EntityFrameworkCore;
using Npgsql;

// #261/#262 — pure unit coverage of the connection-string normalize+validate step
// (no Docker). Mirrors TimeZoneAvailabilityTests: the LOGIC is proven here; the
// integration suite proves it is WIRED into boot. NOT part of the "integration"
// collection, so no Postgres container spins up for these facts.
public sealed class PostgresConnectionStringTests
{
    private static NpgsqlConnectionStringBuilder Normalized(
        string cs, bool isProduction = false, Action<string>? onWarning = null, bool allowInsecure = false)
        => new(PostgresConnectionString.NormalizeAndValidate(cs, isProduction, allowInsecure, onWarning));

    // ---- #261: URI-form translation ----------------------------------------

    [Fact]
    public void Uri_TranslatesEveryComponent()
    {
        var b = Normalized("postgresql://alice:s3cret@dbhost:6543/farmdb");

        Assert.Equal("dbhost", b.Host);
        Assert.Equal(6543, b.Port);
        Assert.Equal("alice", b.Username);
        Assert.Equal("s3cret", b.Password);
        Assert.Equal("farmdb", b.Database);
    }

    [Fact]
    public void Uri_UrlEncodedPassword_IsDecoded()
    {
        // '%40' -> '@', '%3A' -> ':' — the password must be URL-decoded, and the
        // ':' inside it must NOT be mistaken for the user:password separator.
        var b = Normalized("postgresql://user:p%40ss%3Aword@h/db");

        Assert.Equal("user", b.Username);
        Assert.Equal("p@ss:word", b.Password);
    }

    [Fact]
    public void Uri_AbsentPort_DefaultsTo5432()
    {
        var b = Normalized("postgresql://user:pass@h/db");

        Assert.Equal(5432, b.Port);
    }

    [Fact]
    public void Uri_PostgresScheme_IsAlsoAccepted()
    {
        var b = Normalized("postgres://user:pass@h/db");

        Assert.Equal("h", b.Host);
        Assert.Equal("db", b.Database);
    }

    [Fact]
    public void Uri_IPv6Host_KeepsBrackets()
    {
        // System.Uri yields the bracketed literal for an IPv6 host, and Npgsql accepts
        // it verbatim (empirically confirmed to connect). Lock the behaviour in.
        var b = Normalized("postgresql://user:pass@[::1]:5432/db");

        Assert.Equal("[::1]", b.Host);
        Assert.Equal(5432, b.Port);
    }

    [Fact]
    public void Uri_SslModeQueryParam_MapsToBuilderKey()
    {
        var b = Normalized("postgresql://user:pass@h/db?sslmode=require");

        Assert.Equal(SslMode.Require, b.SslMode);
    }

    [Fact]
    public void Uri_HyphenatedSslMode_IsNormalizedToEnum()
    {
        // libpq spells the strong modes "verify-ca"/"verify-full"; Npgsql's own
        // parser REJECTS the hyphenated form, so the translator must normalize it.
        var b = Normalized("postgresql://user:pass@h/db?sslmode=verify-full");

        Assert.Equal(SslMode.VerifyFull, b.SslMode);
    }

    [Fact]
    public void Uri_VerifyFull_TranslatesHostAndMode()
    {
        // Asserts the RESULTING host + mode from the translated key-value string —
        // not merely "did not throw" (UseNpgsql defers parsing, so a null-check alone
        // passes even if translation never ran).
        var b = Normalized("postgresql://u:p@dbhost/db?sslmode=verify-full", isProduction: true);

        Assert.Equal("dbhost", b.Host);
        Assert.Equal("db", b.Database);
        Assert.Equal(SslMode.VerifyFull, b.SslMode);
    }

    [Fact]
    public void Uri_LegacySslTrueFlag_MapsToRequire()
    {
        var b = Normalized("postgresql://u:p@h/db?ssl=true");

        Assert.Equal(SslMode.Require, b.SslMode);
    }

    [Fact]
    public void Uri_DuplicateQueryKey_IsLastWins()
    {
        // A duplicate key must NOT comma-join (which could yield an undefined SslMode);
        // last value wins.
        var b = Normalized("postgresql://u:p@h/db?sslmode=disable&sslmode=verify-full");

        Assert.Equal(SslMode.VerifyFull, b.SslMode);
    }

    [Fact]
    public void Uri_ManagedUrl_KnownParamsMapped_UnknownSkippedWithWarning()
    {
        // A managed-Postgres URL shape (RFC 2606 example host — no provider name): sslmode
        // plus the SCRAM anti-MITM control channel_binding, common libpq params, and one
        // param with no Npgsql equivalent. Npgsql 10.0.3 SUPPORTS channel_binding /
        // target_session_attrs under spaced names, so those must be MAPPED (not dropped);
        // gssencmode has no equivalent -> skipped-with-warning. Must boot, not throw.
        var warnings = new List<string>();
        var b = Normalized(
            "postgresql://user:pass@db.example.com/appdb?sslmode=require&channel_binding=require" +
            "&application_name=cluckwork&connect_timeout=10&target_session_attrs=read-write" +
            "&gssencmode=disable",
            isProduction: true, onWarning: warnings.Add);

        Assert.Equal("db.example.com", b.Host);
        Assert.Equal(SslMode.Require, b.SslMode);
        Assert.Equal("cluckwork", b.ApplicationName);            // application_name  -> "Application Name"
        Assert.Equal(10, b.Timeout);                             // connect_timeout   -> "Timeout"
        Assert.Equal("Require", b["Channel Binding"]!.ToString()); // channel_binding -> "Channel Binding" (MAPPED)
        Assert.Contains("Target Session Attributes", b.ConnectionString); // target_session_attrs (MAPPED)
        var skip = Assert.Single(warnings, w => w.Contains("gssencmode"));
        Assert.Contains("ignored", skip);
    }

    [Fact]
    public void Uri_BadValueOnKnownKeyword_Throws_NotSilentlyDropped()
    {
        // connect_timeout maps to the real Npgsql "Timeout" keyword; a garbage value must
        // SURFACE (fail startup) rather than be swallowed like an unknown parameter — even
        // alongside a valid, TLS-passing sslmode.
        Assert.ThrowsAny<ArgumentException>(() => PostgresConnectionString.NormalizeAndValidate(
            "postgresql://u:p@db.example.com/db?sslmode=verify-full&connect_timeout=garbage",
            isProduction: true));
    }

    // ---- #261: key-value passthrough ---------------------------------------

    [Fact]
    public void KeyValue_IsPassedThroughByteForByte()
    {
        // A Testcontainers-style plaintext key-value string must survive untouched.
        const string kv = "Host=localhost;Port=5432;Database=cluckwork;Username=postgres;Password=postgres";

        var result = PostgresConnectionString.NormalizeAndValidate(kv, isProduction: false);

        Assert.Equal(kv, result);
    }

    // ---- #262: production TLS floor (allow-list, fail closed) ---------------

    [Theory]
    [InlineData("Host=h;Username=u;Password=p;SSL Mode=Disable")]
    [InlineData("Host=h;Username=u;Password=p;SSL Mode=Allow")]
    [InlineData("Host=h;Username=u;Password=p;SSL Mode=Prefer")]
    [InlineData("Host=h;Username=u;Password=p")] // no sslmode -> Npgsql default 'Prefer' (weak)
    public void Production_WeakOrUnsetSslMode_Throws(string cs)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => PostgresConnectionString.NormalizeAndValidate(cs, isProduction: true));

        Assert.Contains("TLS", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sslmode", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Host=h;Username=u;Password=p;SSL Mode=99")] // undefined numeric mode
    [InlineData("Host=h;Username=u;Password=p;SSL Mode=7")]  // undefined (flags-join artifact)
    public void Production_UndefinedSslMode_Throws(string cs)
    {
        // CRITICAL (#1): the floor is an allow-list. An undefined SslMode (which Npgsql
        // parses without error to e.g. (SslMode)99) must NOT slip through as a no-op —
        // it does not guarantee TLS, so it must fail closed.
        var ex = Assert.Throws<InvalidOperationException>(
            () => PostgresConnectionString.NormalizeAndValidate(cs, isProduction: true));

        Assert.Contains("TLS", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Production_WeakSslMode_ViaUri_AlsoThrows()
    {
        Assert.Throws<InvalidOperationException>(
            () => PostgresConnectionString.NormalizeAndValidate(
                "postgresql://u:p@h/db?sslmode=disable", isProduction: true));
    }

    [Fact]
    public void Production_Require_DoesNotThrow_ButWarns()
    {
        var warnings = new List<string>();

        var result = PostgresConnectionString.NormalizeAndValidate(
            "Host=h;Username=u;Password=p;SSL Mode=Require", isProduction: true, onWarning: warnings.Add);

        // Assert the RESULTING mode, not a substring of the input.
        Assert.Equal(SslMode.Require, new NpgsqlConnectionStringBuilder(result).SslMode);
        var warning = Assert.Single(warnings);
        Assert.Contains("VerifyFull", warning, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Host=h;Username=u;Password=p;SSL Mode=VerifyCA")]
    [InlineData("Host=h;Username=u;Password=p;SSL Mode=VerifyFull")]
    public void Production_VerifiedTls_IsOk_WithNoWarning(string cs)
    {
        var warnings = new List<string>();

        var ex = Record.Exception(
            () => PostgresConnectionString.NormalizeAndValidate(cs, isProduction: true, onWarning: warnings.Add));

        Assert.Null(ex);
        Assert.Empty(warnings);
    }

    [Fact]
    public void NonProduction_PlaintextConnection_IsNotEnforced()
    {
        // The Testcontainers integration suite connects plaintext, non-Production.
        var warnings = new List<string>();

        var ex = Record.Exception(() => PostgresConnectionString.NormalizeAndValidate(
            "Host=h;Username=u;Password=p;SSL Mode=Disable", isProduction: false, onWarning: warnings.Add));

        Assert.Null(ex);
        Assert.Empty(warnings);
    }

    // ---- #262: AllowInsecureConnection opt-out -----------------------------

    [Fact]
    public void Production_WeakSslMode_WithAllowInsecure_BootsWithLoudWarning()
    {
        var warnings = new List<string>();

        var ex = Record.Exception(() => PostgresConnectionString.NormalizeAndValidate(
            "Host=db;Username=u;Password=p", isProduction: true,
            allowInsecureConnection: true, onWarning: warnings.Add));

        Assert.Null(ex);
        var warning = Assert.Single(warnings);
        Assert.Contains("AllowInsecureConnection", warning);
        Assert.Contains("UNENCRYPTED", warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Production_WeakSslMode_WithoutAllowInsecure_StillThrows()
    {
        Assert.Throws<InvalidOperationException>(() => PostgresConnectionString.NormalizeAndValidate(
            "Host=db;Username=u;Password=p", isProduction: true, allowInsecureConnection: false));
    }

    [Fact]
    public void AllowInsecure_DoesNotSuppress_VerifyFullSilence()
    {
        // The flag only downgrades the weak-mode throw; verified TLS stays silent.
        var warnings = new List<string>();

        var ex = Record.Exception(() => PostgresConnectionString.NormalizeAndValidate(
            "Host=h;Username=u;Password=p;SSL Mode=VerifyFull", isProduction: true,
            allowInsecureConnection: true, onWarning: warnings.Add));

        Assert.Null(ex);
        Assert.Empty(warnings);
    }

    // ---- wired class: PostgresDbContextConfigurator ------------------------
    // Normalization/validation now happens ONCE at startup; the configurator just
    // consumes the precomputed string. Prove it actually wires UseNpgsql (no Docker —
    // UseNpgsql builds options, it does not open a connection).

    [Fact]
    public void Configurator_Configure_BuildsNpgsqlOptions()
    {
        var options = new DbContextOptionsBuilder();

        new PostgresDbContextConfigurator().Configure(
            options, "Host=h;Username=u;Password=p", new DatabaseResilienceOptions());

        Assert.True(options.IsConfigured);
    }
}
