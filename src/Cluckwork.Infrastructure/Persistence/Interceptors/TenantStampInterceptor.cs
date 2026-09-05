namespace Cluckwork.Infrastructure.Persistence.Interceptors;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
// An unresolved tenant disables checking entirely, deliberately: the CLI
// verbs, the seeders' pre-checks and AppDbContextDesignTimeFactory all run
// that way by design (the seeders themselves resolve the tenant before they
// write).
//
// This interceptor is ONE of two layers (#562). It can only judge the values
// the change tracker holds, and for an entity that reached SaveChanges
// detached — DbSet.Update, DbSet.Remove, Attach on a hand-built stub — those
// values are the caller's, not the database's. The second layer is the
// database: AccountId is a concurrency token on every entity that carries one
// (AppDbContext.OnModelCreating), so the UPDATE/DELETE the database runs
// carries "AND AccountId = <original>" and a stub naming another farm's row
// matches nothing. ThrowingConcurrencyException below is where that refusal
// is heard.
//
// The logger is optional because AppDbContextDesignTimeFactory and the
// migration tests construct this by hand with no logging in reach; the
// serving process gets it from DI.
public sealed class TenantStampInterceptor(
    TenantContext tenant,
    ILogger<TenantStampInterceptor>? logger = null) : SaveChangesInterceptor
{
    private readonly ILogger<TenantStampInterceptor> logger =
        logger ?? NullLogger<TenantStampInterceptor>.Instance;

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

    // #562 — the database-side refusal, heard here. With AccountId a
    // concurrency token, a write whose row belongs to another farm fails as
    // DbUpdateConcurrencyException: zero rows matched the AccountId conjunct.
    // From inside the process that is indistinguishable from an ordinary
    // Version race, and telling them apart would take a second round trip on
    // the failure path — so this does not try. It logs every concurrency
    // failure that happens under a RESOLVED tenant as a security event naming
    // the entity, its key and the tenant, and returns the result UNCHANGED so
    // EF throws exactly as before (Program.cs maps it to 409). A run of these
    // for one tenant on rows it does not own is the signal the event exists
    // for; a lone one is usually a race. Owner decision, 2026-09-02.
    //
    // This is the ThrowingConcurrencyException hook, not SaveChangesFailed:
    // EF routes a concurrency conflict through this dedicated interception
    // point and never reaches SaveChangesFailed for it, which the preflight
    // of this change observed. TenantWriteRefusalLoggingTests pins the hook.
    public override InterceptionResult ThrowingConcurrencyException(
        ConcurrencyExceptionEventData eventData,
        InterceptionResult result)
    {
        LogDatabaseRefusal(eventData);
        return base.ThrowingConcurrencyException(eventData, result);
    }

    public override ValueTask<InterceptionResult> ThrowingConcurrencyExceptionAsync(
        ConcurrencyExceptionEventData eventData,
        InterceptionResult result,
        CancellationToken ct = default)
    {
        LogDatabaseRefusal(eventData);
        return base.ThrowingConcurrencyExceptionAsync(eventData, result, ct);
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
                // OriginalValue is the database's only for an entity that was
                // LOADED while tracked; for a detached stub it is the caller's
                // own, and this check passes it. That is the case the concurrency
                // token closes (#562): the statement then carries
                // "AND AccountId = <original>", and because this check has
                // already required original == tenant, a row that is not the
                // tenant's matches nothing. DetachedTenantWriteTests pins it, and
                // TrackedMutationReadTests keeps the tracked-read precondition as
                // defence in depth.
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
        // #673 — anything that is not a Guid is unCHECKABLE, not exempt. This
        // used to return, so a null Guid? was inserted unstamped and every
        // later write to that row went unverified.
        if (prop.CurrentValue is not Guid accountId)
            throw TenantAccountIdShapeException.ForWrite(
                entry.Metadata.ClrType.Name, nameof(EntityState.Added), prop.CurrentValue);

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
        // #673 — same fail-closed rule as StampOrVerifyAdded: a value that is
        // not a Guid cannot be compared to the tenant, so refuse the write
        // rather than let it past unchecked.
        if (value is not Guid accountId)
            throw TenantAccountIdShapeException.ForWrite(
                entry.Metadata.ClrType.Name, entry.State.ToString(), value);

        if (accountId != tenant.AccountId)
            throw new TenantWriteMismatchException(
                entry.Metadata.ClrType.Name, entry.State.ToString(), tenant.AccountId, accountId);
    }

    private void LogDatabaseRefusal(ConcurrencyExceptionEventData eventData)
    {
        if (!tenant.IsResolved) return;

        // This method must never change the exception the caller sees.
        // Program.cs maps DbUpdateConcurrencyException to 409, and
        // IdentityProvider and IdempotencyMiddleware catch it by type; a sink
        // or entry-shape failure in here would otherwise propagate in its
        // place as a 500. Anything thrown while logging is dropped — the
        // refusal itself is still thrown by EF the moment this returns, and
        // the only channel this method has is the one that just failed. Pinned
        // by TenantWriteRefusalLoggingTests.LoggerFailure_DoesNotChangeTheException.
        try
        {
            foreach (var entry in eventData.Exception.Entries)
            {
                // An owned entry (a Money on a table-split aggregate) shares its
                // owner's key, so the key logged is the row's either way.
                var key = entry.Metadata.FindPrimaryKey();
                var keyValues = key is null
                    ? "?"
                    : string.Join(",", key.Properties.Select(p => entry.Property(p.Name).CurrentValue));

                logger.LogWarning(
                    "{SecurityEvent} entity={EntityType} key={KeyValues} tenant={TenantAccountId}",
                    SecurityEvents.TenantWriteRefusedByDatabase,
                    entry.Metadata.DisplayName(),
                    keyValues,
                    tenant.AccountId);
            }
        }
        catch (Exception)
        {
            // Deliberately silent — see above.
        }
    }
}
