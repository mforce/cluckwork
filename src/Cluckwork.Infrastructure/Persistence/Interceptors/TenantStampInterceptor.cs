namespace Cluckwork.Infrastructure.Persistence.Interceptors;

using Cluckwork.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

// Stamps AccountId on every newly inserted entity so writes can't be mis-tagged
// even if a handler forgets to pass it (tech spec §4.2, point 3), AND refuses
// any tracked write whose AccountId is not the resolved tenant's (#546).
//
// Before #546 this only FILLED an empty AccountId on Added entities: an
// explicitly WRONG non-empty value was written without complaint, and Modified
// and Deleted were never inspected at all. Reads have 27 fail-closed query
// filters; writes had convention. This is the write side's chokepoint.
//
// Matching is by property NAME rather than by base type, and that is
// load-bearing: RefreshToken, IdempotencyRecord and SimulationSeedState all
// carry AccountId WITHOUT inheriting Entity<TId>, so a type-based test would
// silently drop them out of scope — including RefreshToken, which is exactly
// the cross-tenant write /auth/login can attempt once #532 lands.
//
// An unresolved tenant disables checking entirely, deliberately: the seeders,
// the one-shot CLI verbs and AppDbContextDesignTimeFactory all run that way by
// design.
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

        // Only the three states EF actually emits SQL for. Unchanged and
        // Detached write nothing, so inspecting them would reject writes that
        // never happen.
        foreach (var entry in context.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            // Any entity carrying an AccountId property, Entity<TId> or not.
            var prop = entry.Properties
                .FirstOrDefault(p => p.Metadata.Name == nameof(Entity<Guid>.AccountId));
            if (prop is null) continue;

            switch (entry.State)
            {
                case EntityState.Added:
                    StampOrVerifyAdded(entry, prop);
                    break;

                // BOTH the value being written and the value the row was loaded
                // with must be the tenant's. Checking only the current value
                // would let a row loaded under IgnoreQueryFilters be RELABELLED
                // into the current tenant and pass — theft, not a leak.
                //
                // PRECONDITION, stated because it is easy to over-read this
                // (#561 review): OriginalValue is database provenance only for
                // an entity that was LOADED while tracked. DbSet.Update and
                // DbSet.Remove ATTACH a detached instance and seed its original
                // values from the caller's own current values, so a hand-built
                // stub carrying another tenant's primary key and this tenant's
                // AccountId would satisfy both checks and the UPDATE would key
                // on the primary key alone.
                //
                // That is not reachable today: every repository mutation read
                // (GetByIdAsync / GetTrackedAsync / GetByIdLockedAsync) is a
                // TRACKED read behind the tenant query filter, so the snapshot
                // really is the database's — and TrackedMutationReadTests pins
                // exactly that, so switching one of them to AsNoTracking goes
                // red here rather than silently voiding the theft check.
                // Closing it by construction is #562.
                case EntityState.Modified:
                    Verify(entry, prop.OriginalValue);
                    Verify(entry, prop.CurrentValue);
                    break;

                // A delete writes no new value, so the loaded one is all there
                // is to check.
                case EntityState.Deleted:
                    Verify(entry, prop.OriginalValue);
                    break;
            }
        }
    }

    private void StampOrVerifyAdded(EntityEntry entry, PropertyEntry prop)
    {
        if (prop.CurrentValue is not Guid accountId) return;

        if (accountId == Guid.Empty)
        {
            prop.CurrentValue = tenant.AccountId;
            return;
        }

        if (accountId != tenant.AccountId)
            throw new TenantWriteMismatchException(
                entry.Metadata.ClrType.Name, nameof(EntityState.Added), tenant.AccountId, accountId);
    }

    private void Verify(EntityEntry entry, object? value)
    {
        if (value is not Guid accountId) return;

        if (accountId != tenant.AccountId)
            throw new TenantWriteMismatchException(
                entry.Metadata.ClrType.Name, entry.State.ToString(), tenant.AccountId, accountId);
    }
}
