namespace Cluckwork.Api.IntegrationTests;

using System.Data;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Expenses;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// #123 / #159 review — why UpdateFarmSettingsHandler does NOT use SERIALIZABLE
// to close §4.6's read-then-write window.
//
// The first attempt did exactly that, on the reasoning that the probe's scans
// would take predicate locks and a concurrent insert would fail to serialize.
// These tests were written to prove it and disproved it instead: Postgres only
// tracks read-write conflicts among transactions that are ALL serializable, and
// every money-writing handler runs at the default isolation. The interleaving
// stays invisible and the change commits — with or without a transaction around
// the writer.
//
// They drive the database directly, in the exact query shapes the probe and the
// settings write use, because no test that goes through HTTP can interleave two
// transactions on purpose. Kept as executable evidence: anyone reaching for
// SERIALIZABLE here again will see it fail to help before they ship it.
[Collection(IntegrationCollection.Name)]
public sealed class CurrencyLockSerializationTests(CluckworkWebApplicationFactory factory)
{
    private async Task<Guid> SeedFarmAsync()
    {
        var accountId = Guid.NewGuid();
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            db.Accounts.Add(Account.Create(accountId, "Race Farm", "UTC", "USD"));
            await db.SaveChangesAsync();
        });
        return accountId;
    }

    private static async Task InsertAnExpenseAsync(AppDbContext db, Guid accountId)
    {
        var categoryId = Guid.NewGuid();
        db.ExpenseCategories.Add(ExpenseCategory.Create(
            categoryId, accountId, SeedDefaults.FarmId, $"Cat-{categoryId:N}"[..12]));
        db.Expenses.Add(Expense.Create(
            Guid.NewGuid(), accountId, SeedDefaults.FarmId, categoryId,
            DateOnly.FromDateTime(DateTime.UtcNow.Date), "Feed delivery", 12_00, "USD", 2));
        await db.SaveChangesAsync();
    }

    // The probe, in the shape CurrencyBoundRowProbe uses.
    private static Task<bool> ProbeAsync(AppDbContext db) => db.Expenses.AnyAsync();

    private static async Task ChangeCurrencyAsync(AppDbContext db)
    {
        var account = await db.Accounts.SingleAsync();
        account.UpdateSettings(
            account.Name, account.TimeZoneId, account.Locale, "JPY",
            account.UnitSystem, account.FirstDayOfWeek, null, null,
            financialRowsExist: false);
        await db.SaveChangesAsync();
    }

    // Runs the interleaving the guard exists for:
    //   A: begin, probe → "no money rows"
    //   B: begin, insert the farm's first expense, commit
    //   A: write the new currency, commit
    // Returns the SQLSTATE A failed with, or null if A committed.
    private async Task<string?> RaceAsync(IsolationLevel isolationLevel, bool writerIsTransactional)
    {
        var accountId = await SeedFarmAsync();

        using var scopeA = factory.Services.CreateScope();
        scopeA.ServiceProvider.GetRequiredService<TenantContext>().Resolve(accountId);
        var dbA = scopeA.ServiceProvider.GetRequiredService<AppDbContext>();

        await using var transactionA = await dbA.Database.BeginTransactionAsync(isolationLevel);
        Assert.False(await ProbeAsync(dbA), "the farm must start with no money rows for the race to mean anything");

        // B commits entirely inside A's window, reading the account's currency
        // first exactly as every money-writing handler does.
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            if (writerIsTransactional)
            {
                await using var transactionB = await db.Database.BeginTransactionAsync();
                await db.Accounts.AsNoTracking().SingleAsync();
                await InsertAnExpenseAsync(db, accountId);
                await transactionB.CommitAsync();
            }
            else
            {
                // The read and the insert are separate autocommit statements —
                // CreateExpenseHandler's shape.
                await db.Accounts.AsNoTracking().SingleAsync();
                await InsertAnExpenseAsync(db, accountId);
            }
        });

        try
        {
            await ChangeCurrencyAsync(dbA);
            await transactionA.CommitAsync();
            return null;
        }
        catch (Exception ex) when (Sqlstate(ex) is not null)
        {
            return Sqlstate(ex);
        }
    }

    private static string? Sqlstate(Exception ex) => ex switch
    {
        Npgsql.PostgresException pg => pg.SqlState,
        { InnerException: not null } => Sqlstate(ex.InnerException),
        _ => null
    };

    // The writer wrapped in its own transaction (RecordPayment/RecordPurchase's
    // shape). Still not detected: that transaction is READ COMMITTED, so its
    // reads and writes never enter SSI's conflict graph.
    [Fact]
    public async Task Serializable_DoesNotDetectATransactionalWriter() =>
        Assert.Null(await RaceAsync(IsolationLevel.Serializable, writerIsTransactional: true));

    // The writer doing read-then-insert as separate autocommit statements
    // (CreateExpenseHandler's shape). Not detected either.
    [Fact]
    public async Task Serializable_DoesNotDetectAnAutocommitWriter() =>
        Assert.Null(await RaceAsync(IsolationLevel.Serializable, writerIsTransactional: false));

    // The baseline, for contrast: the default isolation behaves the same, which
    // is the point — SERIALIZABLE bought nothing here.
    [Fact]
    public async Task ReadCommitted_DoesNotDetectItEither() =>
        Assert.Null(await RaceAsync(IsolationLevel.ReadCommitted, writerIsTransactional: true));

    // What DOES help, and is what the handler actually does: probe again inside
    // the transaction, immediately before committing. Each statement under READ
    // COMMITTED takes a fresh snapshot, so the second probe sees the row the
    // first one missed and the change is refused.
    [Fact]
    public async Task ARepeatedProbe_SeesTheRowTheFirstOneMissed()
    {
        var accountId = await SeedFarmAsync();

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Resolve(accountId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await using var transaction = await db.Database.BeginTransactionAsync();
        Assert.False(await ProbeAsync(db));

        await factory.WithTenantScopeAsync(accountId, other => InsertAnExpenseAsync(other, accountId));

        Assert.True(await ProbeAsync(db));
    }
}
