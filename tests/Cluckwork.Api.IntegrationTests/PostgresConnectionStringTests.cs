namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Infrastructure.Providers.Postgres;
using Microsoft.EntityFrameworkCore;
using Npgsql;

// #261/#262 — pure unit coverage of the connection-string normalize+validate step
// (no Docker). Mirrors TimeZoneAvailabilityTests: the LOGIC is proven here; the
// integration suite proves it is WIRED into boot. NOT part of the "integration"
// collection, so no Postgres container spins up for these facts.
public sealed class PostgresConnectionStringTests
{
    private static NpgsqlConnectionStringBuilder Normalized(string cs, bool isProduction = false, Action<string>? onWarning = null)
        => new(PostgresConnectionString.NormalizeAndValidate(cs, isProduction, onWarning));

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

    // ---- #261: key-value passthrough ---------------------------------------

    [Fact]
    public void KeyValue_IsPassedThroughByteForByte()
    {
        // A Testcontainers-style plaintext key-value string must survive untouched.
        const string kv = "Host=localhost;Port=5432;Database=cluckwork;Username=postgres;Password=postgres";

        var result = PostgresConnectionString.NormalizeAndValidate(kv, isProduction: false);

        Assert.Equal(kv, result);
    }

    // ---- #262: production TLS floor ----------------------------------------

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
            "Host=h;Username=u;Password=p;SSL Mode=Require", isProduction: true, warnings.Add);

        Assert.Contains("SSL Mode=Require", result);
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
            () => PostgresConnectionString.NormalizeAndValidate(cs, isProduction: true, warnings.Add));

        Assert.Null(ex);
        Assert.Empty(warnings);
    }

    [Fact]
    public void NonProduction_PlaintextConnection_IsNotEnforced()
    {
        // The Testcontainers integration suite connects plaintext, non-Production.
        var warnings = new List<string>();

        var ex = Record.Exception(() => PostgresConnectionString.NormalizeAndValidate(
            "Host=h;Username=u;Password=p;SSL Mode=Disable", isProduction: false, warnings.Add));

        Assert.Null(ex);
        Assert.Empty(warnings);
    }

    // ---- wired class: PostgresDbContextConfigurator ------------------------
    // Configure() only builds options (UseNpgsql does not open a connection), so
    // these need no Docker — they prove the isProduction flag reaches the floor.

    [Fact]
    public void Configurator_Production_PlaintextConnection_ThrowsOnConfigure()
    {
        var configurator = new PostgresDbContextConfigurator(isProduction: true);

        Assert.Throws<InvalidOperationException>(() => configurator.Configure(
            new DbContextOptionsBuilder(), "Host=h;Username=u;Password=p;SSL Mode=Disable"));
    }

    [Fact]
    public void Configurator_NonProduction_PlaintextConnection_Configures()
    {
        var configurator = new PostgresDbContextConfigurator(isProduction: false);

        var ex = Record.Exception(() => configurator.Configure(
            new DbContextOptionsBuilder(), "Host=h;Username=u;Password=p;SSL Mode=Disable"));

        Assert.Null(ex);
    }

    [Fact]
    public void Configurator_Production_UriWithVerifyFull_IsTranslatedAndConfigures()
    {
        var configurator = new PostgresDbContextConfigurator(isProduction: true);

        // URI form (#261) accepted AND certificate-validated TLS (#262) — Npgsql
        // parses the translated key-value string without throwing.
        var ex = Record.Exception(() => configurator.Configure(
            new DbContextOptionsBuilder(), "postgresql://u:p@h/db?sslmode=verify-full"));

        Assert.Null(ex);
    }
}
