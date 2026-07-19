namespace Cluckwork.Infrastructure.Repositories;

using Cluckwork.Application.Features.Expenses;
using Cluckwork.Domain.Expenses;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

public sealed class ExpenseCategoryRepository(AppDbContext db) : IExpenseCategoryRepository
{
    // Reads rely on the tenant query filter (AccountId == current tenant).
    public Task<ExpenseCategory?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.ExpenseCategories.FirstOrDefaultAsync(c => c.Id == id, ct);

    public async Task<IReadOnlyList<ExpenseCategory>> ListActiveAsync(Guid farmId, CancellationToken ct = default) =>
        await db.ExpenseCategories
            .AsNoTracking()
            .Where(c => c.Active && c.FarmId == farmId)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<ExpenseCategory>> ListAllAsync(CancellationToken ct = default) =>
        await db.ExpenseCategories
            .AsNoTracking()
            .OrderBy(c => c.Name)
            .ToListAsync(ct);

    public Task<bool> NameExistsAsync(
        Guid farmId, string name, Guid? excludeId = null, CancellationToken ct = default)
    {
        var normalized = name.Trim().ToLower();
        return db.ExpenseCategories.AnyAsync(
            c => c.FarmId == farmId
                 && c.Name.ToLower() == normalized
                 && (excludeId == null || c.Id != excludeId),
            ct);
    }

    public async Task AddAsync(ExpenseCategory entity, CancellationToken ct = default) =>
        await db.ExpenseCategories.AddAsync(entity, ct);

    public void Update(ExpenseCategory entity) => db.ExpenseCategories.Update(entity);

    public void Remove(ExpenseCategory entity) => db.ExpenseCategories.Remove(entity);
}

public sealed class ExpenseRepository(AppDbContext db) : IExpenseRepository
{
    public Task<Expense?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.Expenses.FirstOrDefaultAsync(e => e.Id == id, ct);

    private IQueryable<Expense> Filtered(DateOnly? from, DateOnly? to, Guid? categoryId) =>
        db.Expenses
            .Where(e => (from == null || e.Date >= from)
                     && (to == null || e.Date <= to)
                     && (categoryId == null || e.ExpenseCategoryId == categoryId));

    public async Task<IReadOnlyList<Expense>> ListAsync(
        DateOnly? from, DateOnly? to, Guid? categoryId, int limit, int offset,
        CancellationToken ct = default) =>
        await Filtered(from, to, categoryId)
            .AsNoTracking()
            // Id tiebreaker: Date alone is non-unique, and unstable ordering
            // under OFFSET paging drops or duplicates rows across pages.
            .OrderByDescending(e => e.Date).ThenByDescending(e => e.Id)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

    // Server-side period total under the same filters — pages must not be
    // summed client-side. long accumulator: SUM of bigint stays bigint.
    public async Task<long> SumAsync(
        DateOnly? from, DateOnly? to, Guid? categoryId, CancellationToken ct = default) =>
        await Filtered(from, to, categoryId)
            .SumAsync(e => e.AmountMinorUnits, ct);

    public async Task AddAsync(Expense entity, CancellationToken ct = default) =>
        await db.Expenses.AddAsync(entity, ct);

    public void Update(Expense entity) => db.Expenses.Update(entity);

    public void Remove(Expense entity) => db.Expenses.Remove(entity);
}
