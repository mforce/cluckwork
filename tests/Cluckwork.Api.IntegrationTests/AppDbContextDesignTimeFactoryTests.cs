namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

// #318 — `dotnet ef` design-time tooling fails closed: no predictable postgres/postgres
// fallback, and every target is held to the SAME Production TLS floor (#261/#262) as a real
// boot, except an explicitly acknowledged LOOPBACK development target. Pure unit coverage
// (no Docker) mirroring PostgresConnectionStringTests — the floor's LOGIC lives there; this
// locks how the design-time factory decides what connection string to hand it.
//
// Mutates process environment variables, so tests run sequentially within THIS class (xunit's
// default — no parallelization is enabled within a test class) and every test restores both
// env vars in a finally block to avoid leaking state into the next test.
public sealed class AppDbContextDesignTimeFactoryTests : IDisposable
{
    private readonly string? _originalConnection =
        Environment.GetEnvironmentVariable(AppDbContextDesignTimeFactory.ConnectionEnvVar);
    private readonly string? _originalLoopbackOptIn =
        Environment.GetEnvironmentVariable(AppDbContextDesignTimeFactory.AllowInsecureLoopbackEnvVar);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(
            AppDbContextDesignTimeFactory.ConnectionEnvVar, _originalConnection);
        Environment.SetEnvironmentVariable(
            AppDbContextDesignTimeFactory.AllowInsecureLoopbackEnvVar, _originalLoopbackOptIn);
    }

    private static void SetConnection(string? value) =>
        Environment.SetEnvironmentVariable(AppDbContextDesignTimeFactory.ConnectionEnvVar, value);

    private static void SetLoopbackOptIn(string? value) =>
        Environment.SetEnvironmentVariable(
            AppDbContextDesignTimeFactory.AllowInsecureLoopbackEnvVar, value);

    // ---- missing / blank config ---------------------------------------------

    [Fact]
    public void UnsetConnectionEnvVar_ThrowsActionableMessage_NamingTheVariable()
    {
        SetConnection(null);
        SetLoopbackOptIn(null);

        var ex = Record.Exception(() => new AppDbContextDesignTimeFactory().CreateDbContext([]));

        Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains(AppDbContextDesignTimeFactory.ConnectionEnvVar, ex!.Message);
    }

    [Fact]
    public void BlankConnectionEnvVar_Throws()
    {
        SetConnection("   ");
        SetLoopbackOptIn(null);

        var ex = Record.Exception(() => new AppDbContextDesignTimeFactory().CreateDbContext([]));

        Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains(AppDbContextDesignTimeFactory.ConnectionEnvVar, ex!.Message);
    }

    [Fact]
    public void RejectedConnection_ErrorDoesNotLeakTheConnectionCredentials()
    {
        // A connection string that fails validation must not be echoed back whole — only the
        // sslmode/host detail needed to act on the failure. Use a runtime-generated password
        // sentinel so a leak would show up as a literal match here (never a hardcoded credential).
        var sentinelPassword = Guid.NewGuid().ToString("N");
        SetConnection($"Host=db.internal.example;Username=u;Password={sentinelPassword}");
        SetLoopbackOptIn(null);

        var ex = Assert.Throws<InvalidOperationException>(
            () => new AppDbContextDesignTimeFactory().CreateDbContext([]));

        Assert.DoesNotContain(sentinelPassword, ex.Message);
    }

    // ---- no default credential (#318 core regression) -----------------------

    [Fact]
    public void UnsetConnectionEnvVar_NeverFallsBackToLocalPostgresCredential()
    {
        // The bug being fixed: an absent env var used to fall back to a predictable
        // Host=localhost;...;Username=postgres;Password=postgres connection. Pin the specific
        // "no default target" wording rather than merely "it throws" — a mutant that
        // reintroduces the fallback would still throw (the TLS floor now also rejects that
        // fallback host), but with a DIFFERENT message, so this assertion still catches it.
        SetConnection(null);
        SetLoopbackOptIn(null);

        var ex = Assert.Throws<InvalidOperationException>(
            () => new AppDbContextDesignTimeFactory().CreateDbContext([]));

        Assert.Contains("no default target", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("postgres/postgres", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- non-loopback target: full production TLS floor, no escape hatch ----

    [Fact]
    public void NonLoopbackHost_PlaintextConnection_WithoutOptIn_Throws()
    {
        SetConnection("Host=db.internal.example;Username=u;Password=p");
        SetLoopbackOptIn(null);

        var ex = Assert.Throws<InvalidOperationException>(
            () => new AppDbContextDesignTimeFactory().CreateDbContext([]));

        Assert.Contains("TLS", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NonLoopbackHost_PlaintextConnection_WithLoopbackOptIn_StillThrows()
    {
        // The escape hatch is scoped to loopback; setting it against a remote host must NOT
        // silently widen coverage to "any host".
        SetConnection("Host=db.internal.example;Username=u;Password=p");
        SetLoopbackOptIn("true");

        var ex = Assert.Throws<InvalidOperationException>(
            () => new AppDbContextDesignTimeFactory().CreateDbContext([]));

        Assert.Contains("loopback", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            AppDbContextDesignTimeFactory.AllowInsecureLoopbackEnvVar, ex.Message);
    }

    [Fact]
    public void NonLoopbackHost_WeakRemoteTls_ViaUri_Throws()
    {
        SetConnection("postgresql://u:p@db.internal.example/farmdb?sslmode=require".Replace(
            "require", "prefer"));
        SetLoopbackOptIn(null);

        var ex = Assert.Throws<InvalidOperationException>(
            () => new AppDbContextDesignTimeFactory().CreateDbContext([]));

        Assert.Contains("TLS", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- loopback target: opt-in required for plaintext ----------------------

    [Fact]
    public void LoopbackHost_PlaintextConnection_WithoutOptIn_Throws()
    {
        // Plaintext to loopback is not on by default either — the operator must
        // explicitly acknowledge it, even for "just localhost".
        SetConnection("Host=localhost;Database=cluckwork_migrations;Username=u;Password=p");
        SetLoopbackOptIn(null);

        Assert.Throws<InvalidOperationException>(
            () => new AppDbContextDesignTimeFactory().CreateDbContext([]));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    public void LoopbackHost_PlaintextConnection_WithOptIn_Succeeds(string host)
    {
        SetConnection($"Host={host};Database=cluckwork_migrations;Username=u;Password=p");
        SetLoopbackOptIn("true");

        using var context = new AppDbContextDesignTimeFactory().CreateDbContext([]);

        Assert.NotNull(context);
    }

    [Fact]
    public void LoopbackHost_UriForm_WithOptIn_Succeeds()
    {
        SetConnection("postgresql://u:p@localhost/cluckwork_migrations");
        SetLoopbackOptIn("true");

        using var context = new AppDbContextDesignTimeFactory().CreateDbContext([]);

        var builder = new NpgsqlConnectionStringBuilder(context.Database.GetConnectionString());
        Assert.Equal("localhost", builder.Host);
        Assert.Equal("cluckwork_migrations", builder.Database);
    }

    // ---- properly TLS'd connections succeed regardless of host --------------

    [Fact]
    public void VerifyFullConnection_NonLoopbackHost_SucceedsWithoutOptIn()
    {
        SetConnection(
            "Host=db.internal.example;Username=u;Password=p;SSL Mode=VerifyFull");
        SetLoopbackOptIn(null);

        using var context = new AppDbContextDesignTimeFactory().CreateDbContext([]);

        Assert.NotNull(context);
    }

    [Fact]
    public void VerifyFullConnection_ViaUri_NormalizesAndSucceeds()
    {
        SetConnection("postgresql://u:p@db.internal.example:6543/farmdb?sslmode=verify-full");
        SetLoopbackOptIn(null);

        using var context = new AppDbContextDesignTimeFactory().CreateDbContext([]);

        var builder = new NpgsqlConnectionStringBuilder(context.Database.GetConnectionString());
        Assert.Equal("db.internal.example", builder.Host);
        Assert.Equal(6543, builder.Port);
        Assert.Equal("farmdb", builder.Database);
        Assert.Equal(SslMode.VerifyFull, builder.SslMode);
    }

    [Fact]
    public void VerifyCaConnection_LoopbackHost_SucceedsWithoutOptIn()
    {
        // VerifyCA/VerifyFull never need the loopback opt-in — it only downgrades the
        // "no TLS at all" failure.
        SetConnection("Host=localhost;Username=u;Password=p;SSL Mode=VerifyCA");
        SetLoopbackOptIn(null);

        using var context = new AppDbContextDesignTimeFactory().CreateDbContext([]);

        Assert.NotNull(context);
    }
}
