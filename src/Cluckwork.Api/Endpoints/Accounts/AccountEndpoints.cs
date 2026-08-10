namespace Cluckwork.Api.Endpoints.Accounts;

using Cluckwork.Application.Features.Accounts;
using Cluckwork.Application.Features.Accounts.UpdateFarmSettings;
using Cluckwork.Domain.Accounts;
using Cluckwork.Api.Configuration;
using Cluckwork.Api.Validation;
using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.Extensions.Options;

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
            .WithSummary("Replace the farm settings (base version required; mismatch is a 409). Currency is locked once anything has recorded an amount in it (§4.6).");

        return group;
    }

    private static async Task<IResult> GetAccount(
        IAccountRepository accounts, IFarmLogoRepository logos, TenantContext tenant, CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();
        var account = await accounts.GetCurrentAsync(ct);
        if (account is null) return Results.NotFound();

        // Hashes only, one round trip (#179 review) — this tells the chrome
        // whether to fetch /logo (or the splash /banner) at all, and gives it a
        // value that changes when the image does.
        var branding = await logos.GetBrandingHashesAsync(ct);
        return Results.Ok(ToResponse(account, branding.LogoContentHash, branding.BannerContentHash));
    }

    private static async Task<IResult> GetSettings(
        IAccountRepository accounts,
        ICurrencyBoundRowProbe currencyBoundRows,
        IFarmLogoRepository logos,
        IOptionsSnapshot<FarmLogoOptions> logoOptions,
        IOptionsSnapshot<FarmBannerOptions> bannerOptions,
        TenantContext tenant,
        CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();
        var account = await accounts.GetCurrentAsync(ct);
        if (account is null) return Results.NotFound();

        // Surfaced so the screen can disable the currency field with an
        // explanation instead of letting the user discover the rule as a 422.
        var canChangeCurrency = !await currencyBoundRows.AnyAsync(ct);
        var branding = await logos.GetBrandingHashesAsync(ct);
        // The upload cap travels with the settings the upload screen reads, so
        // the client-side pre-check and the "up to N MB" copy cannot drift from
        // what the server enforces (#123, #179). It IS config, so the SPA must
        // not carry its own copy.
        return Results.Ok(new FarmSettingsResponse(
            ToResponse(account, branding.LogoContentHash, branding.BannerContentHash),
            canChangeCurrency,
            logoOptions.Value.MaxUploadBytes,
            bannerOptions.Value.MaxUploadBytes));
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
            request.Brand,
            request.DefaultStepperUnit,
            request.Version);

        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            return ValidationResponse.Problem(validation);

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

    private static AccountResponse ToResponse(Account a, string? logoContentHash, string? bannerContentHash) => new(
        a.Id, a.Name,
        a.DefaultCurrencyCode, a.DefaultCurrencyMinorUnit, a.CurrencySymbol,
        a.TimeZoneId, a.Locale,
        a.UnitSystem.ToString(),
        a.FirstDayOfWeek?.ToString(),
        a.DateFormatOverride, a.TimeFormatOverride,
        a.Version,
        logoContentHash,
        a.Brand,
        a.DefaultStepperUnit.ToString(),
        bannerContentHash);
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
    int Version,
    // Null when the farm has no logo — the chrome falls back to app branding
    // and never calls /logo. Otherwise the stored image's content hash, which
    // both identifies the current logo and changes when it is replaced (#123).
    string? LogoContentHash,
    // The farm's accent palette (#149). On the role-agnostic /account read, not
    // just the admin-only settings payload: the palette is farm-wide, so every
    // role's shell needs it to render, and only admins can read /settings.
    string Brand,
    // #444 — the farm-default Daily Entry stepper pack unit, on the same
    // role-agnostic read as Brand: DailyEntryPage needs it for every role,
    // not just admins.
    string DefaultStepperUnit,
    // Null when the farm has no banner — the post-login splash is skipped
    // entirely (#179). Otherwise the stored image's content hash, same
    // self-invalidating role as LogoContentHash.
    string? BannerContentHash);

public sealed record FarmSettingsResponse(
    AccountResponse Settings,
    bool CanChangeCurrency,
    // The farm-logo upload cap in bytes (#123), from config. The SPA reads it
    // for the client-side size pre-check and the "up to N MB" copy.
    int LogoMaxUploadBytes,
    // Same, for the farm banner (#179) — a separate, larger cap.
    int BannerMaxUploadBytes);

public sealed record UpdateFarmSettingsRequest(
    string Name,
    string TimeZoneId,
    string Locale,
    string CurrencyCode,
    string UnitSystem,
    string? FirstDayOfWeek,
    string? DateFormatOverride,
    string? TimeFormatOverride,
    string Brand,
    string DefaultStepperUnit,
    int Version);
