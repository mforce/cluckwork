namespace Cluckwork.Application.Features.Accounts.RemoveFarmLogo;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Common;

// #123 — clear the farm logo, falling the SPA chrome back to app branding.
public sealed class RemoveFarmLogoHandler(
    IFarmLogoRepository logos,
    IUnitOfWork unitOfWork,
    IAuditWriter audit)
{
    public static readonly Error NotSet = Error.NotFound(nameof(FarmLogo), "current");

    public async Task<Result> HandleAsync(CancellationToken ct)
    {
        var logo = await logos.GetTrackedAsync(ct);
        if (logo is null || !logo.HasLogo) return Result.Failure(NotSet);

        // Recorded before the clear, while there is still something to
        // describe. The hash is the useful part: it says WHICH logo went.
        await audit.WriteAsync(
            AuditActions.AccountRemoveLogo, nameof(FarmLogo), logo.Id,
            reason: null,
            details: new { contentType = logo.ContentType, contentHash = logo.ContentHash },
            ct: ct);

        logo.ClearLogo();
        // The row also holds the banner (#179) — delete it only once neither
        // asset is left, never just because this one was.
        if (!logo.HasBanner) logos.Remove(logo);

        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }
}
