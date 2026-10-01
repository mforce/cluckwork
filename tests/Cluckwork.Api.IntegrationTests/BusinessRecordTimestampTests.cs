namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Domain.Sales;
using Microsoft.EntityFrameworkCore;

[Collection(IntegrationCollection.Name)]
public sealed class BusinessRecordTimestampTests(CluckworkWebApplicationFactory factory)
{
    [Fact]
    public async Task Database_stamps_creation_and_tracked_or_bulk_updates()
    {
        var accountId = await factory.SeedAccountWithUserAsync(
            $"timestamps-{Guid.NewGuid():N}@test.local");

        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var customer = Customer.Create(Guid.NewGuid(), accountId, "Timestamp customer", "1");
            db.Customers.Add(customer);
            await db.SaveChangesAsync();

            Assert.NotEqual(default, customer.CreatedAtUtc);
            Assert.Equal(customer.CreatedAtUtc, customer.UpdatedAtUtc);
            var createdAt = customer.CreatedAtUtc;

            await Task.Delay(5);
            Assert.True(customer.Update("Timestamp customer", "2", null, null, null).IsSuccess);
            await db.SaveChangesAsync();

            Assert.Equal(createdAt, customer.CreatedAtUtc);
            Assert.True(customer.UpdatedAtUtc > createdAt);
            var trackedUpdateAt = customer.UpdatedAtUtc;

            await Task.Delay(5);
            await db.Customers
                .Where(row => row.Id == customer.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.Phone, "3")
                    .SetProperty(row => row.Version, row => row.Version + 1));

            db.ChangeTracker.Clear();
            var reloaded = await db.Customers.SingleAsync(row => row.Id == customer.Id);
            Assert.Equal(createdAt, reloaded.CreatedAtUtc);
            Assert.True(reloaded.UpdatedAtUtc > trackedUpdateAt);
        });
    }
}
