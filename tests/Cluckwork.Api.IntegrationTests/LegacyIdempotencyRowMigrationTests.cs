namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Expenses;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

// #307 PR review — the AtomicIdempotencyClaims migration's column defaults
// (RequestHash="", Status=InProgress, LeaseExpiresAt=MinValue) silently
// corrupt every row that already existed under the pre-#307 schema, where
// StatusCode/ResponseBody were NOT NULL: every such row is BY DEFINITION a
// completed cached response, never an in-progress claim. Left un-backfilled,
// a retry against that key could neither steal (RequestHash "" never matches
// a real caller's hash) nor replay (Status isn't Completed) — it 409s
// forever. This lands before #245's InitialCreate squash, so those rows
// really exist in dev/staging/simulation databases.
//
// This reproduces the reviewer's repro exactly: migrate up through the
// migration immediately BEFORE AtomicIdempotencyClaims, hand-insert a row
// shaped exactly like the old schema would have written it, THEN apply
// AtomicIdempotencyClaims, and prove a same-key retry replays instead of
// wedging. Needs its own unmigrated-at-boot factory (the base factory
// normally migrates straight to latest in InitializeAsync), so it is not
// part of the shared IntegrationCollection — same shape as
// MigrateOnStartupDisabledTests's NoBootMigrateFactory.
public sealed class LegacyIdempotencyRowFactory : CluckworkWebApplicationFactory
{
    protected override bool MigrateSchemaOnInitialize => false;
}

public sealed class LegacyIdempotencyRowMigrationTests(LegacyIdempotencyRowFactory factory)
    : IClassFixture<LegacyIdempotencyRowFactory>
{
    // The migration immediately preceding AtomicIdempotencyClaims on this
    // branch (see src/Cluckwork.Infrastructure/Persistence/Migrations/).
    private const string MigrationBeforeAtomicClaims = "20260730053303_AddSimulationSeedState";

    // Reproduces IdempotencyMiddleware.Sha256 independently (same well-known
    // algorithm — sha256 of the UTF8 bytes, lowercase hex — not a reach into
    // internals) so this test can hand-craft a legacy row whose
    // EndpointHash/IdempotencyKeyHash a REAL subsequent request will compute
    // identically.
    private static string Sha256Hex(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static async Task MigrateToAsync(CluckworkWebApplicationFactory f, string? targetMigrationId)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrator.MigrateAsync(targetMigrationId);
    }

    [Fact]
    public async Task LegacyCompletedRow_SurvivesTheMigration_AndStillReplaysInsteadOfWedging()
    {
        // 1. Schema stops just short of AtomicIdempotencyClaims — the "apply
        // InitialCreate" step of the repro (this branch's migrations already
        // include everything up through AddSimulationSeedState).
        await MigrateToAsync(factory, MigrationBeforeAtomicClaims);

        // Only the ACCOUNT and its expense category are created against this
        // deliberately-behind schema — all the hand-inserted legacy row and
        // the later retry need. The login USER is created after step 3
        // instead: #283's AddBaseReferenceDataAndMustChangePassword adds
        // AspNetUsers.MustChangePassword, and the current EF model writes
        // that column on every insert, so a user row simply cannot be written
        // against a schema pinned before it. Moving the user creation (rather
        // than the migration target) keeps the repro itself exact: the legacy
        // idempotency row is still inserted under the pre-#307 schema.
        var email = $"legacy-{Guid.NewGuid():N}@test.local";
        var accountId = Guid.NewGuid();
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            db.Accounts.Add(Account.Create(accountId, "Test Farm Co", "UTC", "USD"));
            await db.SaveChangesAsync();
        });
        var categoryId = Guid.NewGuid();
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            db.ExpenseCategories.Add(ExpenseCategory.Create(
                categoryId, accountId, SeedDefaults.FarmId, "Legacy-Category"));
            await db.SaveChangesAsync();
        });

        var key = Guid.NewGuid().ToString();
        var endpointHash = Sha256Hex("POST:/api/v1/expenses");
        var keyHash = Sha256Hex(key);
        const string legacyContentType = "application/json; charset=utf-8";
        const string legacyResponseBody = """{"id":"11111111-1111-1111-1111-111111111111"}""";

        // 2. Hand-insert a row shaped EXACTLY like the pre-#307 schema wrote
        // it — the only columns that existed then, StatusCode/ResponseBody
        // populated (that schema made them NOT NULL) — a real, completed,
        // cached response.
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO idempotency_records
                    ("Id","AccountId","EndpointHash","IdempotencyKeyHash","StatusCode","ContentType","ResponseBody","CreatedAt")
                VALUES
                    ({Guid.NewGuid()}, {accountId}, {endpointHash}, {keyHash}, 201, {legacyContentType},
                     {legacyResponseBody}, {DateTimeOffset.UtcNow.AddDays(-30)})
                """);
        });

        // 3. Apply the migration under test.
        await MigrateToAsync(factory, null);

        // Now — and only now — the schema can hold a user row (see step 1).
        await factory.SeedUserAsync(accountId, email, asAdmin: true);

        // The backfill flipped it to Completed with the "hash unknown" sentinel.
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var record = await db.IdempotencyRecords.AsNoTracking().SingleAsync(r => r.AccountId == accountId);
            Assert.Equal(IdempotencyStatus.Completed, record.Status);
            Assert.Equal(string.Empty, record.RequestHash);
        });

        // 4. The actual guarantee: a retry against that SAME key replays the
        // legacy cached response — never re-executes, never 409s.
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        var retry = await client.PostWithKeyAsync("/api/v1/expenses", key, new
        {
            expenseCategoryId = categoryId,
            date = DateOnly.FromDateTime(DateTime.UtcNow.Date),
            description = "Should never execute — a legacy replay must short-circuit before the handler",
            amountMinorUnits = 1_00L,
            flockId = (Guid?)null,
            note = (string?)null
        });

        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(legacyResponseBody, await retry.Content.ReadAsStringAsync());

        // Never invoked the handler: the legacy cached response references an
        // id that was never really inserted, and no expense exists at all.
        var count = await factory.WithTenantScopeAsync(accountId, db =>
            db.Expenses.CountAsync(e => e.ExpenseCategoryId == categoryId));
        Assert.Equal(0, count);
    }
}
