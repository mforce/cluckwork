namespace Cluckwork.Infrastructure.Repositories;

using Cluckwork.Application.Features.Accounts;
using Cluckwork.Domain.Accounts;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

public sealed class AccountRepository(AppDbContext db, TenantContext tenant) : IAccountRepository
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
            .FirstOrDefaultAsync(ct);

    public Task<Account?> GetCurrentLockedAsync(CancellationToken ct = default) =>
        db.Accounts.FromSqlInterpolated($"""
            SELECT * FROM "Accounts" WHERE "Id" = {tenant.AccountId} FOR UPDATE
            """)
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(ct);

    public void DiscardChanges(Account account) =>
        db.Entry(account).State = EntityState.Unchanged;
}
