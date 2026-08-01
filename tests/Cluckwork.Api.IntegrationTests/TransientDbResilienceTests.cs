namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.Threading;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Cluckwork.Domain.Sales;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

// #269 — the request path used to have no resilience to a transient DB
// failure: UseNpgsql carried no execution strategy, so a managed-Postgres
// failover or a dropped pooled connection threw straight to a 500. These
// tests prove:
//   1. a TRANSIENT failure mid-request is retried and the request succeeds,
//      exactly once (no duplicated mutation);
//   2. a NON-transient failure is never retried — it fails on the first
//      attempt;
//   3. retries are BOUNDED — a sustained "failure" eventually gives up
//      rather than retrying forever;
//   4. the export's explicit REPEATABLE READ transaction (ExportQueries
//      .BeginConsistentReadAsync) still works now that the DbContext carries
//      a retrying execution strategy (EnableRetryOnFailure forbids a
//      manually-begun transaction outside CreateExecutionStrategy()
//      .ExecuteAsync — see PostgresDbContextConfigurator / AmbientTransaction).
//
// Fault injection: IUnitOfWork is re-registered in ConfigureTestServices to
// decorate the real UnitOfWork and throw a chosen PostgresException on the
// first N calls to SaveChangesAsync — the same "re-register the SCOPED
// SERVICE, not a DbCommandInterceptor on the host's own DbContext" technique
// StepUpAuthTests documents (a second AddDbContext/AddSingleton<IInterceptor>
// against the host's real context silently never fires). PostgresException is
// constructed directly (its public 4-arg constructor takes messageText/
// severity/invariantSeverity/sqlState) rather than produced by an actually
// dropped connection — what NpgsqlRetryingExecutionStrategy.ShouldRetryOn
// checks is PostgresException.IsTransient, which is computed purely from
// SqlState, not from how the exception was constructed. Verified directly
// against the exact Npgsql/EFCore.PG packages this solution references
// before writing these tests: PostgresErrorCodes.CannotConnectNow ("57P03")
// -> IsTransient == true (retried), PostgresErrorCodes.UniqueViolation
// -> IsTransient == false (not retried) — a generic exception would NOT be
// retried at all and would give a test that passes for the wrong reason.
public sealed class TransientFaultState
{
    public int Attempts;
    public int FailCount;
    public string FailSqlState = PostgresErrorCodes.CannotConnectNow;

    // Reset at the top of every [Fact] — tests in this class share ONE host
    // (and therefore one singleton state instance) for speed; xUnit runs
    // [Fact]s within a class sequentially by default, so this is safe without
    // extra locking beyond the Interlocked increment below.
    public void Reset(int failCount, string sqlState)
    {
        Attempts = 0;
        FailCount = failCount;
        FailSqlState = sqlState;
    }
}

internal sealed class FaultInjectingUnitOfWork(IUnitOfWork inner, TransientFaultState state) : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        var attempt = Interlocked.Increment(ref state.Attempts);
        if (attempt <= state.FailCount)
            throw new PostgresException(
                "simulated DB failure (test fault injection)", "FATAL", "FATAL", state.FailSqlState);
        return inner.SaveChangesAsync(ct);
    }

    public Task<bool> ExecuteInTransactionAsync(
        Func<CancellationToken, Task<bool>> operation, CancellationToken ct = default) =>
        inner.ExecuteInTransactionAsync(operation, ct);
}

// Tightens Database:Resilience:MaxRetryDelaySeconds so a retried test finishes
// in well under a second instead of paying Npgsql's real jittered backoff.
public class TransientFaultFactory : CluckworkWebApplicationFactory
{
    public TransientFaultState FaultState { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Database:Resilience:MaxRetryDelaySeconds", "1");
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(FaultState);
            services.AddScoped<IUnitOfWork>(sp => new FaultInjectingUnitOfWork(
                new UnitOfWork(sp.GetRequiredService<AppDbContext>()),
                sp.GetRequiredService<TransientFaultState>()));
        });
    }
}

public sealed class TransientDbResilienceTests(TransientFaultFactory factory)
    : IClassFixture<TransientFaultFactory>
{
    [Fact]
    public async Task TransientFailure_DuringSaveChanges_IsRetried_AndTheRequestSucceedsExactlyOnce()
    {
        var email = $"resilience-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        // Fails the first 2 attempts with a Postgres error the retry
        // strategy classifies transient, then lets the 3rd through.
        factory.FaultState.Reset(failCount: 2, sqlState: PostgresErrorCodes.CannotConnectNow);

        var name = $"Retried Customer {Guid.NewGuid():N}";
        var response = await client.PostWithKeyAsync(
            "/api/v1/customers", Guid.NewGuid().ToString(),
            new { name, phone = "555-0100" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // THE FINDING'S ASSERTION: genuinely retried more than once — not a
        // single lucky pass. Without EnableRetryOnFailure this exact scenario
        // 500s on the FIRST attempt (Attempts == 1, response not 201).
        Assert.True(factory.FaultState.Attempts >= 3,
            $"expected at least 3 SaveChangesAsync attempts (2 failures + 1 success), got {factory.FaultState.Attempts}");

        // Exactly one row — the retried attempt must not have duplicated a
        // mutation a prior (rolled-back) attempt would have produced had it
        // actually committed.
        var count = await factory.WithTenantScopeAsync(accountId,
            db => db.Customers.CountAsync(c => c.Name == name));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task NonTransientFailure_DuringSaveChanges_IsNeverRetried_FailsOnTheFirstAttempt()
    {
        var email = $"resilience-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        // UniqueViolation is NOT transient (PostgresException.IsTransient ==
        // false) — the strategy must propagate on the very first attempt.
        factory.FaultState.Reset(failCount: int.MaxValue, sqlState: PostgresErrorCodes.UniqueViolation);

        var response = await client.PostWithKeyAsync(
            "/api/v1/customers", Guid.NewGuid().ToString(),
            new { name = $"Doomed {Guid.NewGuid():N}", phone = "555-0100" });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(1, factory.FaultState.Attempts);
    }

    [Fact]
    public async Task Export_ExplicitRepeatableReadTransaction_StillWorksUnderTheRetryStrategy()
    {
        var email = $"resilience-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        // No fault this test — proves the explicit-transaction path itself
        // (ExportQueries.BeginConsistentReadAsync) doesn't throw EF's "does
        // not support user-initiated transactions" now that the DbContext
        // carries EnableRetryOnFailure; a pre-#269 configurator never hit
        // this at all (no retry strategy configured), so this specifically
        // guards the wrapping added in ExportQueries.
        factory.FaultState.Reset(failCount: 0, sqlState: PostgresErrorCodes.CannotConnectNow);

        var customerName = $"Export Check {Guid.NewGuid():N}";
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            db.Customers.Add(Customer.Create(
                Guid.NewGuid(), accountId, customerName, "555-0199"));
            await db.SaveChangesAsync();
        });

        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        // /api/v1/export/all, not /export/{dataset}: only ExportAll opens
        // ExportQueries.BeginConsistentReadAsync's REPEATABLE READ snapshot —
        // a single-dataset download never does, so it wouldn't exercise the
        // path this test is about.
        var response = await client.GetAsync("/api/v1/export/all");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var zip = new System.IO.Compression.ZipArchive(
            await response.Content.ReadAsStreamAsync(), System.IO.Compression.ZipArchiveMode.Read);
        var customersEntry = zip.GetEntry("customers.csv");
        Assert.NotNull(customersEntry);
        using var reader = new StreamReader(customersEntry!.Open());
        var csv = await reader.ReadToEndAsync();
        Assert.Contains(customerName, csv);
    }
}

// A SEPARATE, smaller MaxRetryCount so this doesn't need to simulate 7
// failures — needs its own dedicated factory/Postgres (same reasoning as
// FastIdempotencyLeaseFactory/SmallPoolIdempotencyFactory elsewhere in this
// suite: shrinking the shared IntegrationCollection's defaults just to make
// one test fast would affect every other test in the collection).
public sealed class BoundedRetryFactory : TransientFaultFactory
{
    public const int MaxRetryCount = 2;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Database:Resilience:MaxRetryCount", MaxRetryCount.ToString());
    }
}

public sealed class BoundedTransientRetryTests(BoundedRetryFactory factory)
    : IClassFixture<BoundedRetryFactory>
{
    [Fact]
    public async Task TransientFailure_ExceedingMaxRetryCount_EventuallyGivesUp_NeverInfinite()
    {
        var email = $"resilience-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        // Always transient — never lets a single attempt through — proves the
        // strategy gives up after Database:Resilience:MaxRetryCount retries
        // instead of retrying forever.
        factory.FaultState.Reset(failCount: int.MaxValue, sqlState: PostgresErrorCodes.CannotConnectNow);

        var response = await client.PostWithKeyAsync(
            "/api/v1/customers", Guid.NewGuid().ToString(),
            new { name = $"NeverSucceeds {Guid.NewGuid():N}", phone = "555-0100" });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        // Exactly MaxRetryCount + 1: the first attempt, plus every retry —
        // never more (bounded) and never fewer (the retries actually ran).
        Assert.Equal(BoundedRetryFactory.MaxRetryCount + 1, factory.FaultState.Attempts);
    }
}
