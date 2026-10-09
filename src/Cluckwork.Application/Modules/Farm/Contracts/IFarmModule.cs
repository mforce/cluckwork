using Cluckwork.Application.Modules.Farm.Accounts.RemoveFarmBanner;
using Cluckwork.Application.Modules.Farm.Accounts.RemoveFarmLogo;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Modules.Commerce.Contracts;
using Cluckwork.Domain.Modules.Farm.Accounts;
using Cluckwork.Domain.Modules.Farm.Contracts;

namespace Cluckwork.Application.Modules.Farm.Contracts;

// #851: the Farm module's contract. Adapters reach Farm only through the types
// in this Contracts folder. Every member reads or
// writes the current tenant's farm, so it runs only after TenantContext is
// resolved and never establishes identity: sign-in resolves its farm code
// through IIdentityProvider instead.
public interface IFarmModule
{
    static Error LogoNotSet => RemoveFarmLogoHandler.NotSet;

    static Error BannerNotSet => RemoveFarmBannerHandler.NotSet;

    Task<FarmSettingsDetails?> GetSettingsAsync(CancellationToken ct);

    // False once anything has recorded an amount in the farm's currency (§4.6).
    Task<bool> CanChangeCurrencyAsync(CancellationToken ct);

    Task<Result> UpdateSettingsAsync(UpdateFarmSettingsCommand command, CancellationToken ct);

    Task<FarmBrandingHashes> GetBrandingHashesAsync(CancellationToken ct);

    Task<FarmLogoMetadata?> GetLogoMetadataAsync(CancellationToken ct);

    Task<FarmLogoContent?> GetLogoContentAsync(CancellationToken ct);

    Task<Result<FarmLogoMetadata>> SetLogoAsync(
        ReadOnlyMemory<byte> upload, Guid accountId, int maxByteLength, CancellationToken ct);

    Task<Result> RemoveLogoAsync(CancellationToken ct);

    Task<FarmLogoMetadata?> GetBannerMetadataAsync(CancellationToken ct);

    Task<FarmLogoContent?> GetBannerContentAsync(CancellationToken ct);

    Task<Result<FarmLogoMetadata>> SetBannerAsync(
        ReadOnlyMemory<byte> upload, Guid accountId, int maxByteLength, CancellationToken ct);

    Task<Result> RemoveBannerAsync(CancellationToken ct);
}

public sealed record FarmSettingsDetails(
    Guid Id,
    string Name,
    string CurrencyCode,
    int CurrencyMinorUnit,
    string CurrencySymbol,
    string TimeZoneId,
    string Locale,
    UnitSystem UnitSystem,
    DayOfWeek? FirstDayOfWeek,
    string? DateFormatOverride,
    string? TimeFormatOverride,
    string Brand,
    EggUnit DefaultStepperUnit,
    WorkerSaleAllocationPolicy WorkerSaleAllocationPolicy,
    decimal? MaxDiscountPercent,
    bool AllowConnectedApps,
    int Version);
