using Cluckwork.Application.Features.Accounts;
using Cluckwork.Domain.Accounts;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Infrastructure.Repositories;

public sealed class AccountRepository(AppDbContext db, TenantContext tenant) : IAccountRepository, IFarmDirectory
{
    // The account query filter is self-scoped (AccountId == Id == tenant), so
    // FirstOrDefault returns exactly the current tenant's account.
    public Task<Account?> GetCurrentAsync(CancellationToken ct = default) =>
        db.Accounts.AsNoTracking().FirstOrDefaultAsync(ct);

    // Tracked — the settings handler mutates the returned entity and saves it
    // through the shared unit of work (#123).
    public Task<Account?> GetCurrentTrackedAsync(CancellationToken ct = default) =>
        db.Accounts.FirstOrDefaultAsync(ct);

    // #162 — the locking clause must live INSIDE the raw SQL with an explicit
    // tenant WHERE. Composing it with the global query filter would wrap the
    // FOR SHARE/FOR UPDATE in a subquery over ALL accounts, locking every
    // tenant's row. IgnoreQueryFilters is safe exactly because the WHERE
    // reproduces the filter's own predicate (AccountId == Id == tenant).
    public Task<Account?> GetCurrentSharedLockedAsync(CancellationToken ct = default) =>
        db.Accounts.FromSqlInterpolated($"""
            SELECT * FROM "Accounts" WHERE "Id" = {tenant.AccountId} FOR SHARE
            """)
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleOrDefaultAsync(ct);

    public Task<Account?> GetCurrentLockedAsync(CancellationToken ct = default) =>
        db.Accounts.FromSqlInterpolated($"""
            SELECT * FROM "Accounts" WHERE "Id" = {tenant.AccountId} FOR UPDATE
            """)
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(ct);

    // See the port for why IgnoreQueryFilters is mandatory rather than an
    // optimisation. AsNoTracking: login only reads Id and IsActive off this.
    public Task<Account?> FindBySlugAsync(string slug, CancellationToken ct = default)
    {
        var normalized = (slug ?? string.Empty).Trim().ToLowerInvariant();
        return db.Accounts
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Slug == normalized, ct);
    }

    public void DiscardChanges(Account account) =>
        db.Entry(account).State = EntityState.Unchanged;

    // The three IFarmDirectory reads run with no tenant resolved, so the account
    // filter would match Guid.Empty and return nothing: IgnoreQueryFilters is
    // required, not defensive.
    public async Task<IReadOnlyList<FarmTimeZone>> ListTimeZonesAsync(CancellationToken ct = default) =>
        await db.Accounts
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Select(a => new FarmTimeZone(a.Id, a.TimeZoneId))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<FarmListing>> ListAsync(CancellationToken ct = default) =>
        await db.Accounts
            .IgnoreQueryFilters()
            .OrderBy(a => a.Slug)
            .Select(a => new FarmListing(a.Slug, a.Name, a.IsActive))
            .ToListAsync(ct);

    public async Task<Guid?> FindIdBySlugAsync(string slug, CancellationToken ct = default)
    {
        var matches = await db.Accounts
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(account => account.Slug == slug)
            .Select(account => account.Id)
            .ToListAsync(ct);
        // Slug carries a unique index, so 0 or 1. A hand-corrupted database with
        // two matches answers "no such farm" rather than picking one.
        return matches.Count == 1 ? matches[0] : null;
    }
}
