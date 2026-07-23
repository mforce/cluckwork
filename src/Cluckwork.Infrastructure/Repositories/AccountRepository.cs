namespace Cluckwork.Infrastructure.Repositories;

using Cluckwork.Application.Features.Accounts;
using Cluckwork.Domain.Accounts;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

public sealed class AccountRepository(AppDbContext db) : IAccountRepository
{
    // The account query filter is self-scoped (AccountId == Id == tenant), so
    // FirstOrDefault returns exactly the current tenant's account.
    public Task<Account?> GetCurrentAsync(CancellationToken ct = default) =>
        db.Accounts.AsNoTracking().FirstOrDefaultAsync(ct);

    // Tracked — the settings handler mutates the returned entity and saves it
    // through the shared unit of work (#123).
    public Task<Account?> GetCurrentTrackedAsync(CancellationToken ct = default) =>
        db.Accounts.FirstOrDefaultAsync(ct);

    public void DiscardChanges(Account account) =>
        db.Entry(account).State = EntityState.Unchanged;
}
