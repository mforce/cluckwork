namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Cluckwork.Domain.Eggs;
using Cluckwork.Infrastructure.Persistence;
using Cluckwork.Infrastructure.Persistence.Interceptors;
using Cluckwork.Infrastructure.Providers;
using Cluckwork.Infrastructure.Providers.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog.Events;

// #562 — the database-side refusal is logged as a security event.
//
// With AccountId a concurrency token, a write aimed at another farm's row
// fails as DbUpdateConcurrencyException — the same exception an ordinary
// Version race produces, and one that at least seven call sites already
// catch and retry. TenantStampInterceptor's ThrowingConcurrencyException and
// ThrowingConcurrencyExceptionAsync hooks (EF's interception point for that
// exception — it never reaches SaveChangesFailed) therefore log
// every concurrency failure that happens under a RESOLVED tenant as
// Tenant.WriteRefusedByDatabase, naming the entity, its key and the tenant,
// and lets the exception propagate untouched. Owner decision 2026-09-02: a
// run of these for one tenant is the signal; a lone one is usually a race.
//
// Same CollectingSink tap as SecurityEventLoggingTests; events are selected
// by the fresh tenant id each test mints, never by clearing the shared sink.
[Collection(SecurityEventLoggingCollection.Name)]
public sealed class TenantWriteRefusalLoggingTests(SecurityEventLoggingFactory factory)
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

    private static DailyEntry NewEntry(Guid id, Guid accountId, Guid houseId, Guid flockId) =>
        NewEntry(id, accountId, Guid.NewGuid(), houseId, flockId);

    private static DailyEntry NewEntry(Guid id, Guid accountId, Guid farmId, Guid houseId, Guid flockId) =>
        DailyEntry.Create(id, accountId, farmId, houseId, flockId, Today);

    private static string? ScalarOf(LogEvent e, string name) =>
        e.Properties.TryGetValue(name, out var v) && v is ScalarValue s ? s.Value?.ToString() : null;

    private IReadOnlyList<LogEvent> RefusalsFor(Guid tenant) =>
        [.. factory.Sink.Events.Where(e =>
            ScalarOf(e, "SecurityEvent") == SecurityEvents.TenantWriteRefusedByDatabase
            && ScalarOf(e, "TenantAccountId") == tenant.ToString())];

    // Keyed on the ROW, not the tenant: an unresolved context has no tenant to
    // name, so a refusal logged from one would carry Guid.Empty and a tenant
    // filter would never see it — which is exactly the leak this selector
    // must catch. Every test mints a fresh row id, so the set is otherwise empty.
    private IReadOnlyList<LogEvent> RefusalsForRow(Guid rowId) =>
        [.. factory.Sink.Events.Where(e =>
            ScalarOf(e, "SecurityEvent") == SecurityEvents.TenantWriteRefusedByDatabase
            && ScalarOf(e, "KeyValues") == rowId.ToString())];

    private static async Task<Exception?> CaptureAsync(Func<Task> write)
    {
        try { await write(); return null; }
        catch (Exception e) { return e; }
    }

    [Fact]
    public async Task DetachedStubRefusedByTheDatabase_LogsOneSecurityEvent_NamingEntityKeyAndTenant()
    {
        var accountA = await factory.SeedAccountWithUserAsync($"a-{Guid.NewGuid():N}@test.local");
        var accountB = await factory.SeedAccountWithUserAsync($"b-{Guid.NewGuid():N}@test.local");
        var rowId = await factory.WithTenantScopeAsync(accountB, async db =>
        {
            var entry = NewEntry(Guid.NewGuid(), accountB, Guid.NewGuid(), Guid.NewGuid());
            db.DailyEntries.Add(entry);
            await db.SaveChangesAsync();
            return entry.Id;
        });

        var thrown = await CaptureAsync(() => factory.WithTenantScopeAsync(accountA, async db =>
        {
            db.DailyEntries.Update(NewEntry(rowId, accountA, Guid.NewGuid(), Guid.NewGuid()));
            await db.SaveChangesAsync();
        }));

        Assert.True(thrown is DbUpdateConcurrencyException,
            $"stub write was not refused by the database: thrown={thrown?.GetType().Name ?? "none"}");

        var refusal = Assert.Single(RefusalsFor(accountA));
        Assert.Equal(LogEventLevel.Warning, refusal.Level);
        Assert.Equal("DailyEntry", ScalarOf(refusal, "EntityType"));
        Assert.Equal(rowId.ToString(), ScalarOf(refusal, "KeyValues"));
        Assert.Equal(accountA.ToString(), ScalarOf(refusal, "TenantAccountId"));
    }

    // The unresolved path (CLI verbs, the design-time factory, the seeders'
    // pre-checks) has no tenant to name and is not a request: a concurrency
    // failure there is not a security event.
    [Fact]
    public async Task ConcurrencyFailure_UnderAnUnresolvedTenant_LogsNothing()
    {
        var accountA = await factory.SeedAccountWithUserAsync($"a-{Guid.NewGuid():N}@test.local");
        var rowId = await factory.WithTenantScopeAsync(accountA, async db =>
        {
            var entry = NewEntry(Guid.NewGuid(), accountA, Guid.NewGuid(), Guid.NewGuid());
            db.DailyEntries.Add(entry);
            await db.SaveChangesAsync();
            // Bump Version to 1 so a stub built at Version 0 misses the row.
            Assert.True(entry.RecordProduction(100, 1, 1, 0, 0).IsSuccess);
            await db.SaveChangesAsync();
            return entry.Id;
        });

        Exception? thrown;
        using (var scope = factory.Services.CreateScope())
        {
            Assert.False(scope.ServiceProvider.GetRequiredService<TenantContext>().IsResolved);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            thrown = await CaptureAsync(async () =>
            {
                db.DailyEntries.Update(NewEntry(rowId, accountA, Guid.NewGuid(), Guid.NewGuid()));
                await db.SaveChangesAsync();
            });
        }

        Assert.IsType<DbUpdateConcurrencyException>(thrown);
        Assert.Empty(RefusalsForRow(rowId));
    }

    // A failed save that is NOT a concurrency failure — here a duplicate of the
    // live farm/house/flock/day natural key (IX_DailyEntries_NaturalKey, a
    // DbUpdateException carrying 23505) — is not the database refusing a
    // tenant; it must not wear this event.
    [Fact]
    public async Task NonConcurrencyDbUpdateException_LogsNothing()
    {
        var accountA = await factory.SeedAccountWithUserAsync($"a-{Guid.NewGuid():N}@test.local");
        var farmId = Guid.NewGuid();
        var houseId = Guid.NewGuid();
        var flockId = Guid.NewGuid();
        await factory.WithTenantScopeAsync(accountA, async db =>
        {
            db.DailyEntries.Add(NewEntry(Guid.NewGuid(), accountA, farmId, houseId, flockId));
            await db.SaveChangesAsync();
        });

        var before = RefusalsFor(accountA).Count;

        var thrown = await CaptureAsync(() => factory.WithTenantScopeAsync(accountA, async db =>
        {
            db.DailyEntries.Add(NewEntry(Guid.NewGuid(), accountA, farmId, houseId, flockId));
            await db.SaveChangesAsync();
        }));

        Assert.NotNull(thrown);
        Assert.IsNotType<DbUpdateConcurrencyException>(thrown);
        Assert.IsAssignableFrom<DbUpdateException>(thrown);
        Assert.Equal(before, RefusalsFor(accountA).Count);
    }

    // INV-5a (#671 review, round 1): the logging path must never change the
    // exception the caller sees. Program.cs maps DbUpdateConcurrencyException
    // to 409, and IdentityProvider and IdempotencyMiddleware catch it by type;
    // a sink that throws inside the hook would otherwise propagate in its
    // place as a 500. The interceptor is hand-built here with a logger that
    // throws on every call, in the shape the migration tests use, on the
    // factory's own database — with ONE TenantContext shared by the
    // interceptor and the context, as DI wires it (the migration tests pass
    // two unresolved instances, which is fine there and would be wrong here).
    [Fact]
    public async Task LoggerFailure_DoesNotChangeTheException()
    {
        var accountA = await factory.SeedAccountWithUserAsync($"a-{Guid.NewGuid():N}@test.local");
        var accountB = await factory.SeedAccountWithUserAsync($"b-{Guid.NewGuid():N}@test.local");
        var rowId = await factory.WithTenantScopeAsync(accountB, async db =>
        {
            var entry = NewEntry(Guid.NewGuid(), accountB, Guid.NewGuid(), Guid.NewGuid());
            db.DailyEntries.Add(entry);
            await db.SaveChangesAsync();
            return entry.Id;
        });

        var tenant = new TenantContext();
        tenant.Resolve(accountA);
        var sink = new ThrowingLogger();
        var options = new DbContextOptionsBuilder<AppDbContext>();
        new PostgresDbContextConfigurator().Configure(options, factory.ConnectionString, new DatabaseResilienceOptions());
        options.AddInterceptors(new TenantStampInterceptor(tenant, sink));
        await using var db = new AppDbContext(options.Options, tenant, new FlockScope());

        var thrown = await CaptureAsync(async () =>
        {
            db.DailyEntries.Update(NewEntry(rowId, accountA, Guid.NewGuid(), Guid.NewGuid()));
            await db.SaveChangesAsync();
        });

        Assert.True(sink.Calls > 0, "the throwing logger was never invoked — the write never reached the log path");
        Assert.True(thrown is DbUpdateConcurrencyException,
            $"the logger's failure replaced the concurrency exception: thrown={thrown?.GetType().Name ?? "none"} ({thrown?.Message})");
    }

    private sealed class ThrowingLogger : ILogger<TenantStampInterceptor>
    {
        public int Calls { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Calls++;
            throw new InvalidOperationException("log sink exploded on purpose (test double)");
        }
    }

    [Fact]
    public async Task SuccessfulTrackedWrite_LogsNothing()
    {
        var accountA = await factory.SeedAccountWithUserAsync($"a-{Guid.NewGuid():N}@test.local");
        var before = RefusalsFor(accountA).Count;

        await factory.WithTenantScopeAsync(accountA, async db =>
        {
            var entry = NewEntry(Guid.NewGuid(), accountA, Guid.NewGuid(), Guid.NewGuid());
            db.DailyEntries.Add(entry);
            await db.SaveChangesAsync();
            Assert.True(entry.RecordProduction(100, 1, 1, 0, 0).IsSuccess);
            await db.SaveChangesAsync();
        });

        Assert.Equal(before, RefusalsFor(accountA).Count);
    }
}
