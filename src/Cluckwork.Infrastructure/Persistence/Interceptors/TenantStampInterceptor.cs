namespace Cluckwork.Infrastructure.Persistence.Interceptors;

using Cluckwork.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

// Stamps AccountId on every newly inserted entity so writes can't be mis-tagged
// even if a handler forgets to pass it (tech spec §4.2, point 3).
public sealed class TenantStampInterceptor(TenantContext tenant) : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken ct = default)
    {
        StampTenant(eventData.Context);
        return base.SavingChangesAsync(eventData, result, ct);
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        StampTenant(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    private void StampTenant(DbContext? context)
    {
        if (context is null || !tenant.IsResolved) return;

        foreach (var entry in context.ChangeTracker.Entries()
            .Where(e => e.State == EntityState.Added))
        {
            // Stamp any Entity<TId> subclass that has an AccountId property.
            var prop = entry.Properties
                .FirstOrDefault(p => p.Metadata.Name == nameof(Entity<Guid>.AccountId));

            if (prop is not null && prop.CurrentValue is Guid g && g == Guid.Empty)
                prop.CurrentValue = tenant.AccountId;
        }
    }
}
