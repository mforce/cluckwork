using Cluckwork.Application.Modules.Farm.Accounts.RemoveFarmBanner;
using Cluckwork.Application.Modules.Farm.Accounts.RemoveFarmLogo;
using Cluckwork.Application.Modules.Farm.Accounts.SetConnectedApps;
using Cluckwork.Application.Modules.Farm.Accounts.SetFarmBanner;
using Cluckwork.Application.Modules.Farm.Accounts.SetFarmLogo;
using Cluckwork.Application.Modules.Farm.Accounts.UpdateFarmSettings;
using Cluckwork.Application.Modules.Farm.Contracts;
using Cluckwork.Domain.Modules.Farm.Accounts;

namespace Cluckwork.Application.Modules.Farm.Accounts;

public sealed class FarmModule(
    IAccountRepository accounts,
    ICurrencyBoundRowProbe currencyBoundRows,
    IFarmLogoRepository logos,
    UpdateFarmSettingsHandler updateSettings,
    SetConnectedAppsHandler setConnectedApps,
    SetFarmLogoHandler setLogo,
    RemoveFarmLogoHandler removeLogo,
    SetFarmBannerHandler setBanner,
    RemoveFarmBannerHandler removeBanner) : IFarmModule
{
    public async Task<FarmSettingsDetails?> GetSettingsAsync(CancellationToken ct) =>
        await accounts.GetCurrentAsync(ct) is { } account ? ToDetails(account) : null;

    public async Task<bool> CanChangeCurrencyAsync(CancellationToken ct) =>
        !await currencyBoundRows.AnyAsync(ct);

    public Task<Result> UpdateSettingsAsync(UpdateFarmSettingsCommand command, CancellationToken ct) =>
        updateSettings.HandleAsync(command, ct);

    public Task<Result> SetConnectedAppsAsync(SetConnectedAppsCommand command, CancellationToken ct) =>
        setConnectedApps.HandleAsync(command, ct);

    public Task<FarmBrandingHashes> GetBrandingHashesAsync(CancellationToken ct) =>
        logos.GetBrandingHashesAsync(ct);

    public Task<FarmLogoMetadata?> GetLogoMetadataAsync(CancellationToken ct) =>
        logos.GetLogoMetadataAsync(ct);

    public Task<FarmLogoContent?> GetLogoContentAsync(CancellationToken ct) =>
        logos.GetLogoContentAsync(ct);

    public Task<Result<FarmLogoMetadata>> SetLogoAsync(
        ReadOnlyMemory<byte> upload, Guid accountId, int maxByteLength, CancellationToken ct) =>
        setLogo.HandleAsync(upload, accountId, maxByteLength, ct);

    public Task<Result> RemoveLogoAsync(CancellationToken ct) => removeLogo.HandleAsync(ct);

    public Task<FarmLogoMetadata?> GetBannerMetadataAsync(CancellationToken ct) =>
        logos.GetBannerMetadataAsync(ct);

    public Task<FarmLogoContent?> GetBannerContentAsync(CancellationToken ct) =>
        logos.GetBannerContentAsync(ct);

    public Task<Result<FarmLogoMetadata>> SetBannerAsync(
        ReadOnlyMemory<byte> upload, Guid accountId, int maxByteLength, CancellationToken ct) =>
        setBanner.HandleAsync(upload, accountId, maxByteLength, ct);

    public Task<Result> RemoveBannerAsync(CancellationToken ct) => removeBanner.HandleAsync(ct);

    private static FarmSettingsDetails ToDetails(Account a) =>
        new(a.Id, a.Name,
            a.DefaultCurrencyCode, a.DefaultCurrencyMinorUnit, a.CurrencySymbol,
            a.TimeZoneId, a.Locale, a.UnitSystem, a.FirstDayOfWeek,
            a.DateFormatOverride, a.TimeFormatOverride, a.Brand,
            a.DefaultStepperUnit, a.WorkerSaleAllocationPolicy,
            a.MaxDiscount?.Percent, a.AllowConnectedApps, a.Version);
}
