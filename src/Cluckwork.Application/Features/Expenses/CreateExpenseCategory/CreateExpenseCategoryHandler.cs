namespace Cluckwork.Application.Features.Expenses.CreateExpenseCategory;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Expenses;

public sealed class CreateExpenseCategoryHandler(
    IExpenseCategoryRepository categories,
    IUnitOfWork unitOfWork)
{
    public async Task<Result<Guid>> HandleAsync(
        CreateExpenseCategoryCommand command, Guid accountId, CancellationToken ct)
    {
        // Single-farm MVP: categories attach to the seeded farm (grade-catalog
        // convention). Multi-farm picks up a FarmId parameter here.
        var farmId = SeedDefaults.FarmId;

        // Friendly pre-check; the unique index on (account, farm, lower(name))
        // is the real guarantee and races surface as the global 409 mapping.
        if (await categories.NameExistsAsync(farmId, command.Name, excludeId: null, ct))
            return Result.Failure<Guid>(Error.Conflict(
                "ExpenseCategory.DuplicateName",
                $"A category named '{command.Name.Trim()}' already exists."));

        var category = ExpenseCategory.Create(Guid.NewGuid(), accountId, farmId, command.Name);
        await categories.AddAsync(category, ct);
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success(category.Id);
    }
}
