namespace Cluckwork.Api.IntegrationTests;

using System.Data.Common;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Domain.Flocks;
using Cluckwork.Infrastructure.Persistence;
using Cluckwork.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

// #311 — GetProductionAsync's hen-day calculation used to load the account's
// ENTIRE bird-movement ledger (db.BirdMovements.GroupBy(...).ToListAsync()
// with no date filter) and walk it in memory; as farm history grows this cost
// scales with the account's all-time movement count, not with the requested
// range. These tests call ReportQueries directly against a real Postgres
// connection (bypassing the HTTP layer and DI, which need no special
// wiring here) so a DbCommandInterceptor can capture the exact SQL sent — a
// non-flaky way to prove the fix pushes the date bound into SQL, rather than
// inferring it from timing.
[Collection(IntegrationCollection.Name)]
public sealed class ReportQueryBoundingTests(CluckworkWebApplicationFactory factory)
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

    private AppDbContext NewContext(Guid accountId, out SqlCaptureInterceptor capture)
    {
        var tenant = new TenantContext();
        tenant.Resolve(accountId);
        capture = new SqlCaptureInterceptor();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(factory.ConnectionString)
            .AddInterceptors(capture)
            .Options;
        return new AppDbContext(options, tenant, new FlockScope());
    }

    // Seeds a flock with a LARGE number of bird movements strictly before the
    // report's range (simulating years of farm history) plus one movement
    // inside the range, then asserts both (a) the hen-day numbers are exactly
    // what hand computation predicts, and (b) the SQL actually executed bounds
    // BirdMovements by date rather than scanning the ledger unfiltered.
    [Fact]
    public async Task Production_HenDays_AreCorrect_AndBirdMovementQueriesAreDateBounded()
    {
        var accountId = Guid.NewGuid();
        var flockId = Guid.NewGuid();
        var farmId = Guid.NewGuid();
        var from = Today.AddDays(-9);
        var to = Today;
        const int placedDaysBeforeFrom = 520;
        const int outOfRangeMovements = 500; // "years of history" outside the window
        const int onFromBoundaryCull = 5;
        const int inRangeCullOnDayIndex3 = 30;

        await using (var seedDb = NewContext(accountId, out _))
        {
            seedDb.Accounts.Add(Cluckwork.Domain.Accounts.Account.Create(
                accountId, "Bounding Test Farm", "farm-" + accountId.ToString("N")[..12], "UTC", "USD"));
            seedDb.Flocks.Add(Flock.Create(
                flockId, accountId, farmId, Guid.NewGuid(),
                "Bounding Flock", "Test Breed",
                from.AddDays(-placedDaysBeforeFrom), initialCount: 1000));

            // 500 individual out-of-range removals, one per day, far before `from`.
            var seedDate = from.AddDays(-placedDaysBeforeFrom + 1);
            for (var i = 0; i < outOfRangeMovements; i++)
            {
                seedDb.BirdMovements.Add(BirdMovement.Create(
                    Guid.NewGuid(), accountId, flockId,
                    seedDate, BirdMovementType.Cull, quantity: 1));
                seedDate = seedDate.AddDays(1);
            }

            // A removal dated EXACTLY on `from` — the opening-balance/in-range
            // boundary. It must count as in-range (not opening), so a
            // `< from` vs `<= from` off-by-one in the opening-balance query
            // shows up as a wrong Days[0]/Days[1] value below.
            seedDb.BirdMovements.Add(BirdMovement.Create(
                Guid.NewGuid(), accountId, flockId,
                from, BirdMovementType.Cull, quantity: onFromBoundaryCull));

            // One more in-range removal, on the 4th day of the report window (index 3).
            seedDb.BirdMovements.Add(BirdMovement.Create(
                Guid.NewGuid(), accountId, flockId,
                from.AddDays(3), BirdMovementType.Cull,
                quantity: inRangeCullOnDayIndex3));

            await seedDb.SaveChangesAsync();
        }

        await using var db = NewContext(accountId, out var capture);
        var report = await new ReportQueries(db).GetProductionAsync(from, to);

        // 1000 initial − 500 out-of-range removals = 500 opening balance.
        // Each day's own removal doesn't shrink that day's own count
        // (start-of-day convention) — it bites starting the NEXT day.
        Assert.Equal(500, report.Days[0].HenDays); // from: opening balance, before its own -5
        Assert.Equal(495, report.Days[1].HenDays); // from's -5 applied
        Assert.Equal(495, report.Days[2].HenDays);
        Assert.Equal(495, report.Days[3].HenDays); // before its own -30
        Assert.Equal(465, report.Days[4].HenDays); // day 3's -30 applied
        Assert.Equal(465, report.Days[5].HenDays);
        Assert.Equal(465, report.Days[9].HenDays);
        Assert.Equal(500 + 495 * 3 + 465 * 6, report.TotalHenDays);

        var birdMovementCommands = capture.Commands
            .Where(c => c.Sql.Contains("\"BirdMovements\"", StringComparison.Ordinal))
            .ToList();

        // Two SQL-side aggregates (opening balance, in-range per-day) — not one
        // unfiltered scan, and not one query per day/flock (no N+1 against the
        // 500 seeded rows).
        Assert.Equal(2, birdMovementCommands.Count);

        // …and each is bounded by the REQUESTED range, asserted on the parameter
        // values rather than on the shape of the generated SQL. The opening
        // balance carries `from` (Date < from); the in-range aggregate carries
        // both. A query that dropped its date bound would still contain a "<"
        // somewhere, so only the values prove the bound is really there.
        static bool CarriesDate(CapturedCommand command, DateOnly date) =>
            command.Parameters.Any(p => p switch
            {
                DateOnly d => d == date,
                DateTime dt => DateOnly.FromDateTime(dt) == date,
                _ => false,
            });

        Assert.All(birdMovementCommands, command =>
            Assert.True(
                CarriesDate(command, from),
                $"Expected the range start {from:O} as a bound parameter, got: {command.Sql}"));
        Assert.Contains(birdMovementCommands, command => CarriesDate(command, to));
    }

    // A pre-cancelled token must stop the report before it completes, rather
    // than silently running the (potentially expensive) aggregation to
    // completion. Guards the cancellation plumbing threaded through every EF
    // call in GetProductionAsync, including the #311 SQL-pushdown queries.
    [Fact]
    public async Task GetProductionAsync_HonoursAnAlreadyCancelledToken()
    {
        var accountId = Guid.NewGuid();
        await using var db = NewContext(accountId, out _);
        var reportQueries = new ReportQueries(db);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            reportQueries.GetProductionAsync(
                Today.AddDays(-1), Today, new CancellationToken(canceled: true)));
    }
}

// Captures every command EF Core sends to Postgres for inspection — a
// deterministic alternative to inferring query shape from timing or row counts.
internal sealed record CapturedCommand(string Sql, IReadOnlyList<object?> Parameters);

internal sealed class SqlCaptureInterceptor : DbCommandInterceptor
{
    private readonly List<CapturedCommand> _commands = [];

    public IReadOnlyList<CapturedCommand> Commands => _commands;

    // Parameters as well as text: the bound being pushed into SQL is a VALUE,
    // and asserting it by sniffing generated SQL for a "<" is far too loose —
    // "<" appears in "<>" and in plenty of unrelated command text (#311 review).
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        var parameters = command.Parameters.Cast<DbParameter>().Select(p => p.Value).ToList();
        lock (_commands) _commands.Add(new CapturedCommand(command.CommandText, parameters));
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
}
