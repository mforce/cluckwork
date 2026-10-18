using System.ComponentModel;
using Cluckwork.Api.Middleware;
using Cluckwork.Application.Modules.Farm.Contracts;
using Cluckwork.Application.Modules.Finance.Contracts;
using Cluckwork.Application.Modules.FlockManagement.Contracts;
using Cluckwork.Domain.Common;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Cluckwork.Api.Mcp;

// #1313 — expenses are money, behind their own permission and the route's Owner and Manager
// policy. The description is required, as the app requires it, and like the note it is
// stored and never returned (#792). The amount takes effect at once in the finance totals.
[McpServerToolType]
public sealed class ExpenseTools(
    McpCallContext call,
    IdempotentUnit idempotency,
    IFlockLookup flocks,
    IFinanceModule finance,
    IFarmModule farm,
    IValidator<CreateExpenseCommand> validator)
{
    public const string RecordExpense = "cluckwork_record_expense";

    [McpServerTool(Name = RecordExpense, OpenWorld = false)]
    [Description("Record a farm expense, as Record expense does on the Expenses screen. It takes effect at once in the "
        + "expense totals; correct it in Cluckwork.")]
    [Authorize(Policy = AuthPolicies.AdminOnly)]
    [Authorize(Policy = AuthPolicies.ExpensesWriteScope)]
    [MirrorsRoute("CreateExpense")]
    public async Task<RecordedExpense> RecordExpenseAsync(
        [Description(McpWrite.KeyDescription + "Before retrying an older call, ask a person to check Expenses in Cluckwork.")]
        string idempotencyKey,
        [Description("The expense category's name, ignoring case, or its id.")] string category,
        [Description("The day of the expense, YYYY-MM-DD.")] DateOnly date,
        [Description("What it was for. It is stored and never returned.")] string description,
        [Description("The amount, as a decimal in the farm's currency, such as 125.50.")] decimal amount,
        [Description("The flock it was for, by name or id, if any.")] string? flock = null,
        [Description("An optional note. It is stored and never returned.")] string? note = null,
        CancellationToken ct = default) =>
        await McpWrite.RunAsync(idempotency, call, RecordExpense, idempotencyKey,
            new { category, date, description, amount, flock, note },
            async () =>
            {
                var settings = await farm.GetSettingsAsync(ct)
                    ?? throw new McpException("This farm's currency could not be read.");
                var minorUnits = McpMoney.ToMinorUnits(amount, settings.CurrencyMinorUnit, nameof(amount));
                var chosen = McpNames.Resolve(await finance.ListCategoriesAsync(includeInactive: true, ct), category,
                    c => c.Id, c => c.Name, c => c.Active, "expense category", nameof(category));
                var target = string.IsNullOrWhiteSpace(flock) ? null : await FlockTools.ResolveAsync(flocks, flock, ct);
                var command = new CreateExpenseCommand(chosen.Id, date, description, minorUnits, target?.Id, note);
                if (await McpWrite.ValidateAsync(validator, command, "Expense.Invalid", ct) is { IsFailure: true } invalid)
                    return Result.Failure<RecordedExpense>(invalid.Error);
                var id = await finance.CreateExpenseAsync(command, call.AccountId, ct);
                if (id.IsFailure)
                    return Result.Failure<RecordedExpense>(id.Error);
                var stored = (await finance.GetExpenseAsync(id.Value, ct))!;
                if (stored.CurrencyCode != settings.CurrencyCode || stored.CurrencyMinorUnit != settings.CurrencyMinorUnit)
                    return Result.Failure<RecordedExpense>(McpMoney.CurrencyChanged(nameof(amount)));
                return Result.Success(new RecordedExpense(
                    stored.Id, chosen.Id, chosen.Name, date,
                    McpMoney.From(stored.AmountMinorUnits, stored.CurrencyCode, stored.CurrencyMinorUnit),
                    target?.Id, target?.Name, "Correct it with correct on Expenses.", Replayed: false));
            },
            // Expenses are Owner and Manager work, which no flock assignment narrows.
            reply => Task.FromResult(reply with { Replayed = true }),
            ct);
}

public sealed record RecordedExpense(
    Guid ExpenseId, Guid CategoryId, string Category, DateOnly Date, McpMoney Amount, Guid? FlockId, string? FlockName,
    string CorrectInApp, bool Replayed);
