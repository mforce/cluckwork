using Cluckwork.Domain.Expenses;

namespace Cluckwork.Application.Features.Expenses;

public interface IExpenseCategoryRepository
{
    Task<ExpenseCategory?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task AddAsync(ExpenseCategory entity, CancellationToken ct = default);

    // Active categories for the current tenant's farm, name order.
    Task<IReadOnlyList<ExpenseCategory>> ListActiveAsync(Guid farmId, CancellationToken ct = default);

    // Management view: every category of the tenant, inactive included.
    Task<IReadOnlyList<ExpenseCategory>> ListAllAsync(CancellationToken ct = default);

    // Case-insensitive duplicate check within a farm; excludeId skips the
    // category being renamed.
    Task<bool> NameExistsAsync(Guid farmId, string name, Guid? excludeId = null, CancellationToken ct = default);
}

public interface IExpenseRepository
{
    Task<Expense?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task AddAsync(Expense entity, CancellationToken ct = default);

    Task<IReadOnlyList<Expense>> ListAsync(
        DateOnly? from, DateOnly? to, Guid? categoryId, int limit, int offset,
        CancellationToken ct = default);

    // Period total under the same filters — the SPA must not sum pages.
    Task<long> SumAsync(DateOnly? from, DateOnly? to, Guid? categoryId, CancellationToken ct = default);
}
