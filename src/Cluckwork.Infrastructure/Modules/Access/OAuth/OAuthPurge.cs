using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;

namespace Cluckwork.Infrastructure.Modules.Access.OAuth;

public sealed class OAuthPurge(
    AppDbContext db,
    IOpenIddictTokenManager tokens,
    IOpenIddictAuthorizationManager authorizations) : IOAuthPurge
{
    // Order matters: OpenIddict prunes an authorization only once it has no tokens, and
    // an application row cannot go while a token or authorization still references it.
    // PruneAsync removes only tokens that are redeemed, revoked, expired or under a
    // revoked authorization, and authorizations that are revoked or ad hoc with no tokens
    // left. A live connection holds a valid, non-expiring access token (#788) under its
    // authorization, so neither it nor its application is touched.
    public async Task<OAuthPurgeResult> PurgeAsync(
        DateTimeOffset pruneBefore, DateTimeOffset unapprovedBefore, CancellationToken ct)
    {
        var prunedTokens = await tokens.PruneAsync(pruneBefore, ct);
        var prunedAuthorizations = await authorizations.PruneAsync(pruneBefore, ct);

        // Approval creates an authorization, so an application with none, and no token,
        // has never been approved or has nothing left of its approval.
        var unapproved = await db.OAuthApplications
            .Where(application => EF.Property<DateTimeOffset>(application, OAuthApplicationConfiguration.CreatedAtUtc) < unapprovedBefore
                && !application.Authorizations.Any()
                && !application.Tokens.Any())
            .ExecuteDeleteAsync(ct);

        return new(prunedTokens, prunedAuthorizations, unapproved);
    }
}
