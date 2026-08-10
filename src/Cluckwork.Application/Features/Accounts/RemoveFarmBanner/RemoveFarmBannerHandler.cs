namespace Cluckwork.Application.Features.Accounts.RemoveFarmBanner;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Common;

// #179 — clear the farm banner. Mirrors RemoveFarmLogoHandler: clears only the
// banner's own columns, and deletes the shared row only once the logo is also
// unset (see FarmLogo.cs for why the row is shared).
public sealed class RemoveFarmBannerHandler(
    IFarmLogoRepository logos,
    IUnitOfWork unitOfWork,
    IAuditWriter audit)
{
    public static readonly Error NotSet = Error.NotFound(nameof(FarmLogo), "current-banner");

    public async Task<Result> HandleAsync(CancellationToken ct)
    {
        var logo = await logos.GetTrackedAsync(ct);
        if (logo is null || !logo.HasBanner) return Result.Failure(NotSet);

        await audit.WriteAsync(
            AuditActions.AccountRemoveBanner, nameof(FarmLogo), logo.Id,
            reason: null,
            details: new { contentType = logo.BannerContentType, contentHash = logo.BannerContentHash },
            ct: ct);

        logo.ClearBanner();
        if (!logo.HasLogo) logos.Remove(logo);

        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }
}
