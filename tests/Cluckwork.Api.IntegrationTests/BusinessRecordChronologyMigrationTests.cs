namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Sales;
using Cluckwork.Infrastructure.Persistence;
using Cluckwork.Infrastructure.Persistence.Interceptors;
using Cluckwork.Infrastructure.Providers;
using Cluckwork.Infrastructure.Providers.Postgres;
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
        var olderOrderId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var newerOrderId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var createdAt = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var updatedAt = createdAt.AddHours(1);
        var newerOrderCreatedAt = createdAt.AddMinutes(1);

        await InsertCustomerAsync(auditedCustomerId, accountId, "Audited");
        await InsertCustomerAsync(unknownCustomerId, accountId, "Unknown");
        await InsertOrderAsync(olderOrderId, accountId, auditedCustomerId, "SO-OLDER");
        await InsertOrderAsync(newerOrderId, accountId, auditedCustomerId, "SO-NEWER");
        await InsertAuditAsync("Customer.Create", "Customer", auditedCustomerId, createdAt);
        await InsertAuditAsync("Customer.Update", "Customer", auditedCustomerId, updatedAt);
        await InsertAuditAsync("Customer.Read", "Customer", auditedCustomerId, updatedAt.AddHours(1));
        await InsertAuditAsync("SalesOrder.Create", "SalesOrder", olderOrderId, createdAt);
        await InsertAuditAsync("SalesOrder.Create", "SalesOrder", newerOrderId, newerOrderCreatedAt);

        await migrator.MigrateAsync();

        var customers = await db.Customers.IgnoreQueryFilters()
            .Where(row => row.Id == auditedCustomerId || row.Id == unknownCustomerId)
            .ToDictionaryAsync(row => row.Id);
        Assert.Equal(createdAt, customers[auditedCustomerId].CreatedAtUtc);
        Assert.Equal(updatedAt, customers[auditedCustomerId].UpdatedAtUtc);
        var unknown = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(unknown, customers[unknownCustomerId].CreatedAtUtc);
        Assert.Equal(unknown, customers[unknownCustomerId].UpdatedAtUtc);

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

        async Task InsertAuditAsync(string action, string entityType, Guid entityId, DateTimeOffset occurredAt) =>
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "AuditEvents"
                    ("Id", "OccurredAtUtc", "ActorUserId", "ActorEmail", "Action",
                     "EntityType", "EntityId", "AccountId")
                VALUES ({Guid.NewGuid()}, {occurredAt}, {Guid.NewGuid()}, 'migration@test.local',
                        {action}, {entityType}, {entityId}, {accountId})
                """);
    }

    private static AppDbContext BuildContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        new PostgresDbContextConfigurator().Configure(
            options, connectionString, new DatabaseResilienceOptions());
        var tenant = new TenantContext();
        options.AddInterceptors(new TenantStampInterceptor(tenant));
        return new AppDbContext(options.Options, tenant, new FlockScope());
    }
}
