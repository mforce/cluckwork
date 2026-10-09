using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenIddict.Validation;

namespace Cluckwork.Infrastructure.Modules.Access.OAuth;

// #799 — records when a connected app last acted, from the request that validated its
// token. One conditional UPDATE per request, which writes at most once per Interval per
// authorization: a busy app costs one row write every 15 minutes, and "last used" reads
// in hours and days. The database's clock decides, so replicas cannot disagree. A failed
// stamp is logged and the request goes on: the token was already valid.
public sealed class OAuthLastUsedStamp(AppDbContext db, ILogger<OAuthLastUsedStamp> logger)
    : IOpenIddictValidationHandler<OpenIddictValidationEvents.ValidateTokenContext>
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    public async ValueTask HandleAsync(OpenIddictValidationEvents.ValidateTokenContext context)
    {
        if (context.IsRejected || !Guid.TryParse(context.AuthorizationId, out var id))
            return;

        try
        {
            await db.OAuthAuthorizations
                .Where(authorization => authorization.Id == id
                    && (EF.Property<DateTimeOffset?>(authorization, OAuthAuthorizationConfiguration.LastUsedAtUtc) == null
                        || EF.Property<DateTimeOffset?>(authorization, OAuthAuthorizationConfiguration.LastUsedAtUtc)
                            < DateTimeOffset.UtcNow - Interval))
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    authorization => EF.Property<DateTimeOffset?>(authorization, OAuthAuthorizationConfiguration.LastUsedAtUtc),
                    authorization => DateTimeOffset.UtcNow), context.CancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not record the last use of OAuth authorization {AuthorizationId}.", id);
        }
    }
}
