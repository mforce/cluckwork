namespace Cluckwork.Api.Endpoints.Expenses;

using Cluckwork.Api.Validation;
using Cluckwork.Application.Features.Accounts;
using Cluckwork.Application.Features.Audit;
using Cluckwork.Application.Features.Expenses;
using Cluckwork.Application.Features.Expenses.AdjustExpense;
using Cluckwork.Application.Features.Expenses.CreateExpense;
using Cluckwork.Application.Features.Expenses.CreateExpenseCategory;
using Cluckwork.Application.Features.Expenses.UpdateExpenseCategory;
using Cluckwork.Domain.Expenses;
using Cluckwork.Infrastructure.Persistence;
using FluentValidation;

// Money data is admin-only end to end (#87): the agreed split keeps production
// data open to workers and money behind the Admin role, so BOTH groups below
// carry AdminOnly — reads included, unlike the grade catalog.
public static class ExpenseEndpoints
{
    public static RouteGroupBuilder MapExpenseCategoryEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", ListCategories)
            .WithName("ListExpenseCategories")
            .WithSummary("List expense categories. Active only by default; includeInactive=true adds deactivated ones (management view).");

        group.MapPost("/", CreateCategory)
            .WithName("CreateExpenseCategory")
            .WithSummary("Create an expense category (name unique per farm, case-insensitive).");

        group.MapPut("/{id:guid}", UpdateCategory)
            .WithName("UpdateExpenseCategory")
            .WithSummary("Rename a category and/or flip it active/inactive. Deactivation hides it from new expenses; recorded ones keep it.");

        return group;
    }

    public static RouteGroupBuilder MapExpenseEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", ListExpenses)
            .WithName("ListExpenses")
            .WithSummary("List expenses newest first (optional from/to/category filters, paged) with the period total.");

        group.MapGet("/{id:guid}", GetExpense)
            .WithName("GetExpense")
            .WithSummary("Get a single expense.");

        group.MapPost("/", CreateExpense)
            .WithName("CreateExpense")
            .WithSummary("Record an expense. Currency is copied from the account and never changes afterwards.");

        group.MapPut("/{id:guid}", AdjustExpense)
            .WithName("AdjustExpense")
            .WithSummary("Correct an expense in place (base version required; mismatch is a 409). Currency is not editable.");

        return group;
    }

    private const int DefaultPageSize = 100;
    private const int MaxPageSize = 500;

    // --- categories ---

    private static async Task<IResult> ListCategories(
        IExpenseCategoryRepository categories, TenantContext tenant, CancellationToken ct,
        bool includeInactive = false)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();
        var list = includeInactive
            ? await categories.ListAllAsync(ct)
            : await categories.ListActiveAsync(Cluckwork.Domain.Accounts.SeedDefaults.FarmId, ct);
        return Results.Ok(list.Select(ToResponse));
    }

    private static async Task<IResult> CreateCategory(
        CreateExpenseCategoryRequest request,
        CreateExpenseCategoryHandler handler,
        IValidator<CreateExpenseCategoryCommand> validator,
        TenantContext tenant,
        CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        var command = new CreateExpenseCategoryCommand(request.Name);
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            return ValidationResponse.Problem(validation);

        var result = await handler.HandleAsync(command, tenant.AccountId, ct);
        return result.IsSuccess
            ? Results.Created($"/api/v1/expense-categories/{result.Value}", new { Id = result.Value })
            : MapFailure(result.Error);
    }

    private static async Task<IResult> UpdateCategory(
        Guid id,
        UpdateExpenseCategoryRequest request,
        UpdateExpenseCategoryHandler handler,
        IValidator<UpdateExpenseCategoryCommand> validator,
        TenantContext tenant,
        CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        var command = new UpdateExpenseCategoryCommand(id, request.Name, request.Active);
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            return ValidationResponse.Problem(validation);

        var result = await handler.HandleAsync(command, ct);
        return result.IsSuccess ? Results.NoContent() : MapFailure(result.Error);
    }

    // --- expenses ---

    private static async Task<IResult> ListExpenses(
        IExpenseRepository expenses,
        IAccountRepository accounts,
        IAuditEventRepository audit,
        TenantContext tenant,
        CancellationToken ct,
        DateOnly? from = null,
        DateOnly? to = null,
        Guid? categoryId = null,
        int? limit = null,
        int? offset = null)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        var take = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);
        var skip = Math.Max(offset ?? 0, 0);

        var list = await expenses.ListAsync(from, to, categoryId, take, skip, ct);
        var total = await expenses.SumAsync(from, to, categoryId, ct);
        // Single-farm MVP: every expense carries the account currency, so one
        // label fits the total. Multi-currency totals arrive with multi-farm.
        var account = await accounts.GetCurrentAsync(ct);
        var provenance = await audit.GetProvenanceAsync(
            nameof(Expense), list.Select(e => e.Id).ToList(), ct);

        return Results.Ok(new ExpenseListResponse(
            list.Select(e => ToResponse(e, provenance.GetValueOrDefault(e.Id))).ToList(),
            total,
            account?.DefaultCurrencyCode ?? "",
            account?.DefaultCurrencyMinorUnit ?? 2));
    }

    private static async Task<IResult> GetExpense(
        Guid id, IExpenseRepository expenses, IAuditEventRepository audit,
        TenantContext tenant, CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();
        var expense = await expenses.GetByIdAsync(id, ct);
        if (expense is null) return Results.NotFound();
        var provenance = await audit.GetProvenanceAsync(nameof(Expense), [id], ct);
        return Results.Ok(ToResponse(expense, provenance.GetValueOrDefault(id)));
    }

    private static async Task<IResult> CreateExpense(
        CreateExpenseRequest request,
        CreateExpenseHandler handler,
        IValidator<CreateExpenseCommand> validator,
        TenantContext tenant,
        CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        var command = new CreateExpenseCommand(
            request.ExpenseCategoryId, request.Date, request.Description,
            request.AmountMinorUnits, request.FlockId, request.Note);

        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            return ValidationResponse.Problem(validation);

        var result = await handler.HandleAsync(command, tenant.AccountId, ct);
        return result.IsSuccess
            ? Results.Created($"/api/v1/expenses/{result.Value}", new { Id = result.Value })
            : MapFailure(result.Error);
    }

    private static async Task<IResult> AdjustExpense(
        Guid id,
        AdjustExpenseRequest request,
        AdjustExpenseHandler handler,
        IValidator<AdjustExpenseCommand> validator,
        IExpenseRepository expenses,
        IAuditEventRepository audit,
        TenantContext tenant,
        CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        var command = new AdjustExpenseCommand(
            id, request.Version, request.ExpenseCategoryId, request.Date,
            request.Description, request.AmountMinorUnits, request.FlockId, request.Note);

        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            return ValidationResponse.Problem(validation);

        var result = await handler.HandleAsync(command, ct);
        if (result.IsFailure) return MapFailure(result.Error);

        // The corrected row (fresh version) so the client can rebind its edit
        // state without a second round trip. The adjust just wrote its own
        // audit event, so this read reports the correction as the last change.
        var updated = await expenses.GetByIdAsync(id, ct);
        if (updated is null) return Results.NotFound();
        var provenance = await audit.GetProvenanceAsync(nameof(Expense), [id], ct);
        return Results.Ok(ToResponse(updated, provenance.GetValueOrDefault(id)));
    }

    private static IResult MapFailure(Cluckwork.Domain.Common.Error error)
    {
        if (error.Code.EndsWith(".NotFound", StringComparison.Ordinal))
            return Results.NotFound();
        return error.Code is "ExpenseCategory.DuplicateName" or "ExpenseCategory.NotActive"
            or "ExpenseCategory.AlreadyActive" or "Expense.VersionMismatch"
            ? Results.Problem(error.Description, statusCode: StatusCodes.Status409Conflict, title: error.Code)
            : Results.Problem(error.Description, statusCode: 422, title: error.Code);
    }

    private static ExpenseCategoryResponse ToResponse(ExpenseCategory c) =>
        new(c.Id, c.FarmId, c.Name, c.Active);

    private static ExpenseResponse ToResponse(Expense e, EntityProvenance? p) =>
        new(e.Id, e.FarmId, e.ExpenseCategoryId, e.Date, e.Description,
            e.AmountMinorUnits, e.CurrencyCode, e.CurrencyMinorUnit,
            e.FlockId, e.Note, e.Version,
            p?.CreatedByEmail, p?.CreatedAtUtc, p?.LastChangedByEmail, p?.LastChangedAtUtc);
}

public sealed record ExpenseCategoryResponse(Guid Id, Guid FarmId, string Name, bool Active);
public sealed record CreateExpenseCategoryRequest(string Name);
public sealed record UpdateExpenseCategoryRequest(string Name, bool Active);

public sealed record ExpenseResponse(
    Guid Id, Guid FarmId, Guid ExpenseCategoryId, DateOnly Date, string Description,
    long AmountMinorUnits, string CurrencyCode, int CurrencyMinorUnit,
    Guid? FlockId, string? Note, int Version,
    // #494 provenance, derived from the audit trail: null together for a
    // record created before that shipped (no backfill).
    string? CreatedByEmail, DateTimeOffset? CreatedAtUtc,
    string? LastChangedByEmail, DateTimeOffset? LastChangedAtUtc);

public sealed record ExpenseListResponse(
    List<ExpenseResponse> Items, long TotalMinorUnits, string CurrencyCode, int CurrencyMinorUnit);

public sealed record CreateExpenseRequest(
    Guid ExpenseCategoryId, DateOnly Date, string Description,
    long AmountMinorUnits, Guid? FlockId, string? Note);

public sealed record AdjustExpenseRequest(
    int Version, Guid ExpenseCategoryId, DateOnly Date, string Description,
    long AmountMinorUnits, Guid? FlockId, string? Note);
