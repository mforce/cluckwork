namespace Cluckwork.Application.Features.Expenses.UpdateExpenseCategory;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Common;

public sealed class UpdateExpenseCategoryHandler(
    IExpenseCategoryRepository categories,
    IUnitOfWork unitOfWork)
{
    public async Task<Result> HandleAsync(UpdateExpenseCategoryCommand command, CancellationToken ct)
    {
        var category = await categories.GetByIdAsync(command.ExpenseCategoryId, ct);
        if (category is null)
            return Result.Failure(Error.NotFound("ExpenseCategory", command.ExpenseCategoryId));

        if (await categories.NameExistsAsync(category.FarmId, command.Name, excludeId: category.Id, ct))
            return Result.Failure(Error.Conflict(
                "ExpenseCategory.DuplicateName",
                $"A category named '{command.Name.Trim()}' already exists."));

        var rename = category.Rename(command.Name);
        if (rename.IsFailure) return rename;

        // Rename() bumped Version; the flip guards double-toggles, so only call
        // when the flag actually changes.
        if (command.Active != category.Active)
        {
            var flip = command.Active ? category.Activate() : category.Deactivate();
            if (flip.IsFailure) return flip;
        }

        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }
}
