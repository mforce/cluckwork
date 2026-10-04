using Cluckwork.Application.Features.Expenses;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Infrastructure.Repositories;

// Reads rely on the tenant query filter (AccountId == current tenant).
public sealed class FinanceFixture(AppDbContext db) : IFinanceFixture
{
    public async Task<Guid?> FindCategoryIdByNameAsync(string name, CancellationToken ct = default) =>
        (await db.ExpenseCategories.FirstOrDefaultAsync(c => c.Name == name, ct))?.Id;

    public Task<bool> ExpenseExistsAsync(string description, CancellationToken ct = default) =>
        db.Expenses.AnyAsync(e => e.Description == description, ct);

    public async Task<FinanceFixtureCounts> CountAsync(CancellationToken ct = default) =>
        new(await db.ExpenseCategories.CountAsync(ct), await db.Expenses.CountAsync(ct));
}
