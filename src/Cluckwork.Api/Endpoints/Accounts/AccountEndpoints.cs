namespace Cluckwork.Api.Endpoints.Accounts;

using Cluckwork.Application.Common;
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
        IAccountRepository accounts, IFarmLogoRepository logos, TenantContext tenant,
        FlockScope flockScope, ICurrentUser currentUser, CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();
        var account = await accounts.GetCurrentAsync(ct);
        if (account is null) return Results.NotFound();

        // Hashes only, one round trip (#179 review) — this tells the chrome
        // whether to fetch /logo (or the splash /banner) at all, and gives it a
        // value that changes when the image does.
        var branding = await logos.GetBrandingHashesAsync(ct);
        return Results.Ok(ToResponse(
            account, branding.LogoContentHash, branding.BannerContentHash,
            ShowFarmWideSaleAllocationNotice(account, flockScope),
            YourMaxDiscountPercent(account, currentUser)));
    }

    /// <summary>
    /// #727 — the ceiling THIS caller is bound by, or null. Null covers both
    /// "the farm sets none" and "you may exceed it", because both mean the same
    /// thing to the screen: show no ceiling warning.
    /// </summary>
    /// <remarks>
    /// The role here is CLAIMS-derived, so a user promoted mid-session sees a
    /// stale hint until their token refreshes. The failure mode is a stale
    /// warning, never a wrong outcome: ConfirmSaleHandler's fresh
    /// in-transaction role read is the authority, and it re-decides this on
    /// every confirm. The role predicate is shared with that handler so the two
    /// cannot disagree about who is bound.
    /// <para>
    /// GetAccount already materializes the whole Account row for every role, so
    /// this costs no new query.
    /// </para>
    /// </remarks>
    private static decimal? YourMaxDiscountPercent(Account account, ICurrentUser currentUser) =>
        account.MaxDiscount is { } ceiling
        && !Roles.MayExceedDiscountCeiling(Roles.ResolveEffective(currentUser.Roles))
            ? ceiling.Percent
            : null;

    private static async Task<IResult> GetSettings(
        IAccountRepository accounts,
        ICurrencyBoundRowProbe currencyBoundRows,
        IFarmLogoRepository logos,
        IOptionsSnapshot<FarmLogoOptions> logoOptions,
        IOptionsSnapshot<FarmBannerOptions> bannerOptions,
        TenantContext tenant,
        FlockScope flockScope,
        ICurrentUser currentUser,
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
            ToResponse(
                account, branding.LogoContentHash, branding.BannerContentHash,
                ShowFarmWideSaleAllocationNotice(account, flockScope),
                YourMaxDiscountPercent(account, currentUser)),
            canChangeCurrency,
            logoOptions.Value.MaxUploadBytes,
            bannerOptions.Value.MaxUploadBytes,
            account.WorkerSaleAllocationPolicy.ToString(),
            account.MaxDiscount?.Percent));
    }

    // #612 — true only for a restricted plain Worker under AllFarmFlocks: the
    // farm has opted into farm-wide allocation, but THIS caller is still
    // narrowed by their own flock assignments, so the sale screen shows the
    // persistent generic notice. FlockScope is already resolved by
    // FlockScopeResolutionMiddleware before this endpoint runs, from the same
    // UserRoleAssignment rows Confirm reads — no second query, no new state.
    private static bool ShowFarmWideSaleAllocationNotice(Account account, FlockScope flockScope) =>
        account.WorkerSaleAllocationPolicy == WorkerSaleAllocationPolicy.AllFarmFlocks
        && !flockScope.IsUnrestricted;

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
            request.WorkerSaleAllocationPolicy,
            request.Version,
            request.MaxDiscountPercent);

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

    private static AccountResponse ToResponse(
        Account a, string? logoContentHash, string? bannerContentHash,
        bool showFarmWideSaleAllocationNotice, decimal? yourMaxDiscountPercent) => new(
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
        bannerContentHash,
        showFarmWideSaleAllocationNotice,
        yourMaxDiscountPercent);
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
    string? BannerContentHash,
    // #612 — role-agnostic on purpose: true only for a restricted plain
    // Worker under AllFarmFlocks, so the Sales screen can show the persistent
    // generic notice without exposing the raw policy (that lives only on
    // FarmSettingsResponse, admin-only) to every role.
    bool ShowFarmWideSaleAllocationNotice,
    // #727 — the ceiling THIS caller is bound by, or null when the farm sets
    // none OR the caller may exceed it: both mean "show no ceiling warning".
    // Claims-derived and therefore a display hint only — ConfirmSaleHandler's
    // fresh in-transaction role read is the authority. See
    // AccountEndpoints.YourMaxDiscountPercent.
    decimal? YourMaxDiscountPercent);

public sealed record FarmSettingsResponse(
    AccountResponse Settings,
    bool CanChangeCurrency,
    // The farm-logo upload cap in bytes (#123), from config. The SPA reads it
    // for the client-side size pre-check and the "up to N MB" copy.
    int LogoMaxUploadBytes,
    // Same, for the farm banner (#179) — a separate, larger cap.
    int BannerMaxUploadBytes,
    // #612 — the raw policy, admin-only (like CanChangeCurrency above). Every
    // other role only ever sees the derived ShowFarmWideSaleAllocationNotice
    // on the role-agnostic AccountResponse.
    string WorkerSaleAllocationPolicy,
    // #727 — the raw ceiling as a percent, admin-only for the same reason.
    // Null means the farm sets none; zero means "give nothing away".
    decimal? MaxDiscountPercent);

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
    string WorkerSaleAllocationPolicy,
    int Version,
    // #727 — a PERCENT, not basis points: basis points are the storage choice.
    // Omitted or null clears the ceiling; zero is a different, legal setting.
    decimal? MaxDiscountPercent);
