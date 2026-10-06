using Cluckwork.Application.Modules.Farm.Contracts;
using Cluckwork.Domain.Modules.Farm.Accounts;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Infrastructure.Repositories;

public sealed class FarmFixture(AppDbContext db) : IFarmFixture
{
    public Task<bool> AccountExistsAsync(Guid accountId, CancellationToken ct = default) =>
        db.Accounts.IgnoreQueryFilters().AnyAsync(a => a.Id == accountId, ct);

    public Task<int> CountAccountsAsync(CancellationToken ct = default) =>
        db.Accounts.IgnoreQueryFilters().CountAsync(ct);

    public async Task CreateAccountAsync(
        Guid accountId, string name, string slug, string timeZoneId, string currencyCode,
        CancellationToken ct = default)
    {
        db.Accounts.Add(Account.Create(accountId, name, slug, timeZoneId, currencyCode));
        await db.SaveChangesAsync(ct);
    }
}
