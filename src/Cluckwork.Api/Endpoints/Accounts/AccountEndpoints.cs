namespace Cluckwork.Api.Endpoints.Accounts;

using Cluckwork.Application.Features.Accounts;
using Cluckwork.Application.Features.Accounts.UpdateFarmSettings;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.Persistence;
using FluentValidation;

public static class AccountEndpoints
{
    public static RouteGroupBuilder MapAccountEndpoints(this RouteGroupBuilder group)
    {
        // Open to every authenticated role on purpose: §4.5's display rule
        // makes locale, timezone and the currency fields a prerequisite for
        // rendering ANY money, date or number, so a read-only viewer needs them
        // as much as an owner. The write below is the admin-gated half.
        group.MapGet("/", GetAccount)
            .WithName("GetAccount")
            .WithSummary("Current farm: name and the §4.5 localization settings clients need to format money, dates and numbers.");

        group.MapGet("/settings", GetSettings)
            .RequireAuthorization(AuthPolicies.AdminOnly)
            .WithName("GetFarmSettings")
            .WithSummary("Farm settings for the settings screen — the same fields plus whether the currency is still changeable (§4.6).");

        group.MapPut("/settings", UpdateSettings)
            .RequireAuthorization(AuthPolicies.AdminOnly)
            .WithName("UpdateFarmSettings")
            .WithSummary("Replace the farm settings (base version required; mismatch is a 409). Currency is locked once financial rows exist (§4.6).");

        return group;
    }

    private static async Task<IResult> GetAccount(
        IAccountRepository accounts, TenantContext tenant, CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();
        var account = await accounts.GetCurrentAsync(ct);
        return account is null ? Results.NotFound() : Results.Ok(ToResponse(account));
    }

    private static async Task<IResult> GetSettings(
        IAccountRepository accounts,
        IFinancialRowProbe financialRows,
        TenantContext tenant,
        CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();
        var account = await accounts.GetCurrentAsync(ct);
        if (account is null) return Results.NotFound();

        // Surfaced so the screen can disable the currency field with an
        // explanation instead of letting the user discover the rule as a 422.
        var canChangeCurrency = !await financialRows.AnyFinancialRowsAsync(ct);
        return Results.Ok(new FarmSettingsResponse(ToResponse(account), canChangeCurrency));
    }

    private static async Task<IResult> UpdateSettings(
        UpdateFarmSettingsRequest request,
        UpdateFarmSettingsHandler handler,
        IValidator<UpdateFarmSettingsCommand> validator,
        TenantContext tenant,
        CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        var command = new UpdateFarmSettingsCommand(
            request.Name,
            request.TimeZoneId,
            request.Locale,
            request.CurrencyCode,
            request.UnitSystem,
            request.FirstDayOfWeek,
            request.DateFormatOverride,
            request.TimeFormatOverride,
            request.Version);

        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            return Results.ValidationProblem(validation.ToDictionary());

        var result = await handler.HandleAsync(command, ct);
        return result.IsSuccess ? Results.NoContent() : MapFailure(result.Error);
    }

    private static IResult MapFailure(Error error) => error.Code switch
    {
        "Account.NotFound" => Results.NotFound(),
        // Someone else saved between the screen's read and this write.
        "Account.VersionMismatch" => Results.Problem(
            error.Description, statusCode: StatusCodes.Status409Conflict, title: error.Code),
        // Includes Account.CurrencyLocked — a rule violation on well-formed
        // input, which is exactly what 422 is for.
        _ => Results.Problem(error.Description, statusCode: 422, title: error.Code)
    };

    private static AccountResponse ToResponse(Account a) => new(
        a.Id, a.Name,
        a.DefaultCurrencyCode, a.DefaultCurrencyMinorUnit, a.CurrencySymbol,
        a.TimeZoneId, a.Locale,
        a.UnitSystem.ToString(),
        a.FirstDayOfWeek?.ToString(),
        a.DateFormatOverride, a.TimeFormatOverride,
        a.Version);
}

// CurrencyCode/CurrencyMinorUnit keep their names and positions from the
// pre-#123 shape — clients parse money with them.
public sealed record AccountResponse(
    Guid Id,
    string Name,
    string CurrencyCode,
    int CurrencyMinorUnit,
    string CurrencySymbol,
    string TimeZoneId,
    string Locale,
    string UnitSystem,
    string? FirstDayOfWeek,
    string? DateFormatOverride,
    string? TimeFormatOverride,
    int Version);

public sealed record FarmSettingsResponse(AccountResponse Settings, bool CanChangeCurrency);

public sealed record UpdateFarmSettingsRequest(
    string Name,
    string TimeZoneId,
    string Locale,
    string CurrencyCode,
    string UnitSystem,
    string? FirstDayOfWeek,
    string? DateFormatOverride,
    string? TimeFormatOverride,
    int Version);
