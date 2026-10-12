using System.Text.Json;
using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenIddict.EntityFrameworkCore.Models;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Cluckwork.Infrastructure.Modules.Access.OAuth;

// #799 — Connected apps reads and revokes OpenIddict's rows through the model rather
// than its managers, because the managers exist only where the OAuth server runs and
// these screens exist everywhere. OpenIddict's tables carry no AccountId: a farm is its
// users, so every farm-wide query and every revocation goes through AspNetUsers' AccountId.
public sealed class ConnectedApps(
    AppDbContext db, IUnitOfWork unitOfWork, IAuditWriter audit, ILogger<ConnectedApps> logger)
{
    public static readonly TimeSpan LastUsedInterval = TimeSpan.FromMinutes(15);

    public Task<IReadOnlyList<AppConnection>> ListAsync(Guid userId, CancellationToken ct)
    {
        var subject = userId.ToString();
        return LoadAsync(db.OAuthAuthorizations.Where(authorization => authorization.Subject == subject), ct);
    }

    public Task<IReadOnlyList<AppConnection>> ListFarmAsync(Guid accountId, CancellationToken ct)
    {
        var subjects = db.Users.Where(user => user.AccountId == accountId).Select(user => user.Id.ToString());
        return LoadAsync(db.OAuthAuthorizations.Where(authorization => subjects.Contains(authorization.Subject!)), ct);
    }

    // Every valid authorization, not one: two approvals can race and leave two (#798).
    // Validation checks the authorization on every request (#796), so revoking it is what
    // refuses the app next time; its tokens are revoked too, so none outlives it. A new
    // ConcurrencyToken fails any in-flight OpenIddict write of a row (#1229).
    public async Task<Result> DisconnectAsync(Guid accountId, Guid userId, string clientId, CancellationToken ct)
    {
        var subject = userId.ToString();
        var concurrencyToken = Guid.NewGuid().ToString();
        var disconnected = await unitOfWork.ExecuteInTransactionAsync(async token =>
        {
            if (!await db.Users.AnyAsync(user => user.Id == userId && user.AccountId == accountId, token))
                return false;

            var authorizations = db.OAuthAuthorizations.Where(authorization => authorization.Subject == subject
                && authorization.Application!.ClientId == clientId && authorization.Status == Statuses.Valid);
            var appName = await authorizations.Select(authorization => authorization.Application!.DisplayName)
                .FirstOrDefaultAsync(token);
            var revoked = await authorizations.ExecuteUpdateAsync(setters => setters
                .SetProperty(authorization => authorization.Status, Statuses.Revoked)
                .SetProperty(authorization => authorization.ConcurrencyToken, concurrencyToken), token);
            if (revoked == 0)
                return false;

            await db.OAuthTokens
                .Where(row => row.Subject == subject && row.Application!.ClientId == clientId
                    && (row.Status == Statuses.Valid || row.Status == Statuses.Inactive))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.Status, Statuses.Revoked)
                    .SetProperty(row => row.ConcurrencyToken, concurrencyToken), token);
            await audit.WriteAsync(AuditActions.UserAppDisconnected, "User", userId,
                details: new { clientId, appName }, ct: token);
            return true;
        }, ct);

        return disconnected ? Result.Success() : Result.Failure(Error.NotFound("ConnectedApp", clientId));
    }

    // Called by CredentialEpochMiddleware once the account and credential checks pass, so a
    // request they refuse never counts as use (#1229). One conditional UPDATE, which writes at
    // most once per LastUsedInterval per authorization: a busy app costs one row write every
    // 15 minutes, and "last used" reads in hours and days. The database's clock decides, so
    // replicas cannot disagree. A failed stamp is logged and the request goes on.
    public async Task RecordUseAsync(Guid authorizationId, CancellationToken ct)
    {
        try
        {
            await db.OAuthAuthorizations
                .Where(authorization => authorization.Id == authorizationId
                    && (EF.Property<DateTimeOffset?>(authorization, OAuthAuthorizationConfiguration.LastUsedAtUtc) == null
                        || EF.Property<DateTimeOffset?>(authorization, OAuthAuthorizationConfiguration.LastUsedAtUtc)
                            < DateTimeOffset.UtcNow - LastUsedInterval))
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    authorization => EF.Property<DateTimeOffset?>(authorization, OAuthAuthorizationConfiguration.LastUsedAtUtc),
                    authorization => DateTimeOffset.UtcNow), ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not record the last use of OAuth authorization {AuthorizationId}.", authorizationId);
        }
    }

    // #1229 — the credential epoch only moves forward, so a token it superseded never
    // passes again. Every bump revokes the user's connections in its own transaction; this
    // catches a token issued while a bump committed, and any left from before #1229. Only
    // the presented token goes: its authorization may since hold a token from a newer
    // reconnection. A new ConcurrencyToken fails any in-flight OpenIddict write of the row.
    // The refusal stands whether or not this write lands.
    public async Task RevokeSupersededTokenAsync(Guid tokenId, CancellationToken ct)
    {
        var concurrencyToken = Guid.NewGuid().ToString();
        try
        {
            await db.OAuthTokens
                .Where(row => row.Id == tokenId && (row.Status == Statuses.Valid || row.Status == Statuses.Inactive))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.Status, Statuses.Revoked)
                    .SetProperty(row => row.ConcurrencyToken, concurrencyToken), ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not revoke superseded OAuth token {TokenId}.", tokenId);
        }
    }

    private static async Task<IReadOnlyList<AppConnection>> LoadAsync(
        IQueryable<OpenIddictEntityFrameworkCoreAuthorization<Guid>> authorizations, CancellationToken ct)
    {
        var rows = await authorizations
            .Where(authorization => authorization.Status == Statuses.Valid)
            .Select(authorization => new
            {
                authorization.Subject,
                authorization.Application!.ClientId,
                authorization.Application.DisplayName,
                authorization.Scopes,
                authorization.CreationDate,
                LastUsedAtUtc = EF.Property<DateTimeOffset?>(authorization, OAuthAuthorizationConfiguration.LastUsedAtUtc),
            })
            .ToListAsync(ct);

        return [.. rows
            .GroupBy(row => (row.Subject, row.ClientId))
            .Select(app => new AppConnection(
                Guid.Parse(app.Key.Subject!),
                app.Key.ClientId!,
                app.First().DisplayName,
                [.. app.SelectMany(row => JsonSerializer.Deserialize<string[]>(row.Scopes ?? "[]")!).Distinct().Order()],
                new DateTimeOffset(app.Min(row => row.CreationDate!.Value), TimeSpan.Zero),
                app.Max(row => row.LastUsedAtUtc)))
            .OrderByDescending(app => app.ConnectedAtUtc)
            .ThenBy(app => app.ClientId, StringComparer.Ordinal)];
    }
}
