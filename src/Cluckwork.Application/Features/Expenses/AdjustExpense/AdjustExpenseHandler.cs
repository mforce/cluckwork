namespace Cluckwork.Application.Features.Expenses.AdjustExpense;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Flocks;
using Cluckwork.Domain.Common;

public sealed class AdjustExpenseHandler(
    IExpenseRepository expenses,
    IExpenseCategoryRepository categories,
    IFlockRepository flocks,
    IUnitOfWork unitOfWork,
    IAuditWriter audit)
{
    public async Task<Result> HandleAsync(AdjustExpenseCommand command, CancellationToken ct)
    {
        var expense = await expenses.GetByIdAsync(command.ExpenseId, ct);
        if (expense is null)
            return Result.Failure(Error.NotFound("Expense", command.ExpenseId));

        // End-to-end optimistic concurrency (AGENTS.md): the client edits
        // against a base version; a mismatch is a deterministic 409, and the
        // EF concurrency token backstops the racing-save window after this.
        if (expense.Version != command.Version)
            return Result.Failure(Error.Conflict(
                "Expense.VersionMismatch",
                "The expense was changed by someone else. Reload and retry."));

        // Retargeting must pick an ACTIVE category; KEEPING the recorded one is
        // allowed even if it was deactivated since (grandfathering).
        if (command.ExpenseCategoryId != expense.ExpenseCategoryId)
        {
            var category = await categories.GetByIdAsync(command.ExpenseCategoryId, ct);
            if (category is null || category.FarmId != expense.FarmId || !category.Active)
                return Result.Failure(Error.Validation(
                    "Expense.UnknownCategory",
                    "The expense category does not exist or is inactive."));
        }

        if (command.FlockId is { } flockId && flockId != expense.FlockId)
        {
            var flock = await flocks.GetByIdAsync(flockId, ct);
            if (flock is null || flock.FarmId != expense.FarmId)
                return Result.Failure(Error.Validation(
                    "Expense.UnknownFlock", "The flock does not exist on this farm."));
        }

        var previousAmount = expense.AmountMinorUnits;
        var result = expense.Adjust(
            command.ExpenseCategoryId, command.Date, command.Description,
            command.AmountMinorUnits, command.FlockId, command.Note);
        if (result.IsFailure) return result;

        // Same SaveChanges as the change (#93): commits or fails with it.
        await audit.WriteAsync(AuditActions.ExpenseAdjust, nameof(Cluckwork.Domain.Expenses.Expense), expense.Id,
            reason: null,
            new { previousAmountMinorUnits = previousAmount, newAmountMinorUnits = expense.AmountMinorUnits }, ct);

        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }
}
