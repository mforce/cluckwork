namespace Cluckwork.Application.Features.Expenses;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Expenses;

public interface IExpenseCategoryRepository : IRepository<ExpenseCategory, Guid>
{
    // Active categories for the current tenant's farm, name order.
    Task<IReadOnlyList<ExpenseCategory>> ListActiveAsync(Guid farmId, CancellationToken ct = default);

    // Management view: every category of the tenant, inactive included.
    Task<IReadOnlyList<ExpenseCategory>> ListAllAsync(CancellationToken ct = default);

    // Case-insensitive duplicate check within a farm; excludeId skips the
    // category being renamed.
    Task<bool> NameExistsAsync(Guid farmId, string name, Guid? excludeId = null, CancellationToken ct = default);
}

public interface IExpenseRepository : IRepository<Expense, Guid>
{
    Task<IReadOnlyList<Expense>> ListAsync(
        DateOnly? from, DateOnly? to, Guid? categoryId, int limit, int offset,
        CancellationToken ct = default);

    // Period total under the same filters — the SPA must not sum pages.
    Task<long> SumAsync(DateOnly? from, DateOnly? to, Guid? categoryId, CancellationToken ct = default);
}
