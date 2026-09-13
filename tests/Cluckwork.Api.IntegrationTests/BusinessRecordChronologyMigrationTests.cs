namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Sales;
using Cluckwork.Application.Features.Sales;
using Cluckwork.Infrastructure.Persistence;
using Cluckwork.Infrastructure.Persistence.Interceptors;
using Cluckwork.Infrastructure.Providers;
using Cluckwork.Infrastructure.Providers.Postgres;
using Cluckwork.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Testcontainers.PostgreSql;

public sealed class BusinessRecordChronologyMigrationTests
{
    private const string PreviousMigration = "20260911135948_AddAccountMaxDiscountBasisPoints";
    private const string PostgresImage =
        "postgres:18.4-trixie@sha256:3a82e1f56c8f0f5616a11103ac3d47e632c3938698946a7ad26da0df1334744a";

    [Fact]
    public async Task Upgrade_uses_exact_audits_and_marks_unknown_legacy_times()
    {
        await using var postgres = new PostgreSqlBuilder(PostgresImage).Build();
        await postgres.StartAsync();
        await using var db = BuildContext(postgres.GetConnectionString());
        var migrator = db.Database.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration);

        var accountId = SeedDefaults.AccountId;
        var auditedCustomerId = Guid.NewGuid();
        var unknownCustomerId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var olderOrderId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var newerOrderId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var createdAt = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var updatedAt = createdAt.AddHours(1);
        var newerOrderCreatedAt = createdAt.AddMinutes(1);

        await InsertCustomerAsync(auditedCustomerId, accountId, "Audited");
        await InsertCustomerAsync(unknownCustomerId, accountId, "Unknown");
        await InsertUserAsync(userId, accountId);
        await InsertOrderAsync(olderOrderId, accountId, auditedCustomerId, "SO-OLDER");
        await InsertOrderAsync(newerOrderId, accountId, auditedCustomerId, "SO-NEWER");
        await InsertAuditAsync("Customer.Create", "Customer", auditedCustomerId, createdAt);
        await InsertAuditAsync("Customer.Update", "Customer", auditedCustomerId, updatedAt);
        await InsertAuditAsync("Customer.Read", "Customer", auditedCustomerId, updatedAt.AddHours(1));
        await InsertAuditAsync("User.Create", "User", userId, createdAt);
        await InsertAuditAsync("User.RoleChanged", "User", userId, updatedAt);
        await InsertAuditAsync("SalesOrder.Create", "SalesOrder", olderOrderId, createdAt);
        await InsertAuditAsync("SalesOrder.Create", "SalesOrder", newerOrderId, newerOrderCreatedAt);

        await migrator.MigrateAsync();

        var timestampTriggerCount = await db.Database.SqlQueryRaw<int>(
            """
            SELECT count(*)::integer AS "Value"
            FROM pg_trigger AS trigger
            INNER JOIN pg_class AS relation ON relation.oid = trigger.tgrelid
            INNER JOIN pg_namespace AS schema ON schema.oid = relation.relnamespace
            WHERE NOT trigger.tgisinternal
              AND schema.nspname = 'public'
              AND trigger.tgname LIKE 'TR\_%\_BusinessRecordTimestamps' ESCAPE '\'
            """).SingleAsync();
        var timestampedModelCount = db.Model.GetEntityTypes()
            .Count(entity => !entity.IsOwned()
                && typeof(ICreatedRecord).IsAssignableFrom(entity.ClrType));
        Assert.Equal(timestampedModelCount, timestampTriggerCount);

        var customers = await db.Customers.IgnoreQueryFilters()
            .Where(row => row.Id == auditedCustomerId || row.Id == unknownCustomerId)
            .ToDictionaryAsync(row => row.Id);
        Assert.Equal(createdAt, customers[auditedCustomerId].CreatedAtUtc);
        Assert.Equal(updatedAt, customers[auditedCustomerId].UpdatedAtUtc);
        var unknown = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(unknown, customers[unknownCustomerId].CreatedAtUtc);
        Assert.Equal(unknown, customers[unknownCustomerId].UpdatedAtUtc);

        var baseAccount = await db.Accounts.IgnoreQueryFilters()
            .SingleAsync(row => row.Id == SeedDefaults.AccountId);
        Assert.Equal(unknown, baseAccount.CreatedAtUtc);
        Assert.Equal(unknown, baseAccount.UpdatedAtUtc);

        var user = await db.Users.IgnoreQueryFilters().SingleAsync(row => row.Id == userId);
        Assert.Equal(createdAt, user.CreatedAtUtc);
        Assert.Equal(updatedAt, user.UpdatedAtUtc);

        var orders = await db.SalesOrders.IgnoreQueryFilters()
            .Where(row => row.Id == olderOrderId || row.Id == newerOrderId)
            .OrderBy(row => EF.Property<long>(row, "Sequence"))
            .Select(row => new
            {
                row.Id,
                row.CreatedAtUtc,
                Sequence = EF.Property<long>(row, "Sequence"),
            })
            .ToListAsync();
        Assert.Equal([olderOrderId, newerOrderId], orders.Select(row => row.Id));
        Assert.Equal([createdAt, newerOrderCreatedAt], orders.Select(row => row.CreatedAtUtc));
        Assert.Equal([1L, 2L], orders.Select(row => row.Sequence));

        // Legacy rows without creation audits all receive the same explicit
        // unknown timestamp. This repository assertion therefore exercises
        // Sequence as the final ordering key, rather than merely proving that
        // CreatedAtUtc replaced the former random-Guid tiebreak.
        Guid[] tiedOrderIds =
        [
            Guid.Parse("80000000-0000-0000-0000-000000000008"),
            Guid.Parse("10000000-0000-0000-0000-000000000001"),
            Guid.Parse("70000000-0000-0000-0000-000000000007"),
            Guid.Parse("20000000-0000-0000-0000-000000000002"),
            Guid.Parse("60000000-0000-0000-0000-000000000006"),
            Guid.Parse("30000000-0000-0000-0000-000000000003"),
            Guid.Parse("50000000-0000-0000-0000-000000000005"),
            Guid.Parse("40000000-0000-0000-0000-000000000004"),
        ];
        for (var index = 0; index < tiedOrderIds.Length; index++)
            await InsertOrderAsync(tiedOrderIds[index], accountId, auditedCustomerId, $"SO-TIE-{index}");

        // Inserts above run after the migration and therefore receive live
        // timestamps. Pin all to the legitimate legacy sentinel by disabling
        // only the timestamp trigger for this setup operation.
        await db.Database.ExecuteSqlRawAsync(
            """
            ALTER TABLE "SalesOrders" DISABLE TRIGGER "TR_SalesOrders_BusinessRecordTimestamps";
            UPDATE "SalesOrders"
            SET "CreatedAtUtc" = TIMESTAMPTZ '1970-01-01 00:00:00+00'
            WHERE "ReferenceNumber" LIKE 'SO-TIE-%';
            ALTER TABLE "SalesOrders" ENABLE TRIGGER "TR_SalesOrders_BusinessRecordTimestamps";
            """);

        var tenant = new TenantContext();
        tenant.Resolve(accountId);
        await using var listDb = BuildContext(postgres.GetConnectionString(), tenant);
        var list = await new SalesOrderRepository(listDb).ListAsync(
            new SalesOrderListFilter(null, null, null, null, SettlementScope.Hidden), 100, 0);
        Assert.Equal(
            tiedOrderIds.Reverse(),
            list.Select(row => row.Order.Id)
                .Where(tiedOrderIds.Contains)
                .ToArray());

        async Task InsertCustomerAsync(Guid id, Guid tenantId, string name) =>
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Customers" ("Id", "Name", "Phone", "AccountId", "Version")
                VALUES ({id}, {name}, '1', {tenantId}, 0)
                """);

        async Task InsertOrderAsync(Guid id, Guid tenantId, Guid customerId, string reference) =>
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "SalesOrders"
                    ("Id", "ReferenceNumber", "CustomerId", "Status", "OrderDate",
                     "TotalMinorUnits", "TotalCurrencyCode", "TotalCurrencyMinorUnit",
                     "Version", "AccountId")
                VALUES ({id}, {reference}, {customerId}, 'Draft', DATE '2025-01-02',
                        0, 'USD', 2, 0, {tenantId})
                """);

        async Task InsertUserAsync(Guid id, Guid tenantId) =>
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "AspNetUsers"
                    ("Id", "AccountId", "MustChangePassword", "UserName", "NormalizedUserName",
                     "Email", "NormalizedEmail", "EmailConfirmed", "PhoneNumberConfirmed",
                     "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount", "CredentialEpoch",
                     "StepUpLogoutEpoch")
                VALUES ({id}, {tenantId}, FALSE, 'migration-user@test.local',
                        'MIGRATION-USER@TEST.LOCAL', 'migration-user@test.local',
                        'MIGRATION-USER@TEST.LOCAL', FALSE, FALSE, FALSE, FALSE, 0, 1, 0)
                """);

        async Task InsertAuditAsync(string action, string entityType, Guid entityId, DateTimeOffset occurredAt) =>
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "AuditEvents"
                    ("Id", "OccurredAtUtc", "ActorUserId", "ActorEmail", "Action",
                     "EntityType", "EntityId", "AccountId")
                VALUES ({Guid.NewGuid()}, {occurredAt}, {Guid.NewGuid()}, 'migration@test.local',
                        {action}, {entityType}, {entityId}, {accountId})
                """);
    }

    private static AppDbContext BuildContext(string connectionString, TenantContext? tenant = null)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        new PostgresDbContextConfigurator().Configure(
            options, connectionString, new DatabaseResilienceOptions());
        var activeTenant = tenant ?? new TenantContext();
        options.AddInterceptors(new TenantStampInterceptor(activeTenant));
        return new AppDbContext(options.Options, activeTenant, new FlockScope());
    }
}
