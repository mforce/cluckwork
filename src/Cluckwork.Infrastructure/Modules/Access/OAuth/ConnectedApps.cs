using System.Text.Json;
using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using OpenIddict.EntityFrameworkCore.Models;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Cluckwork.Infrastructure.Modules.Access.OAuth;

// #799 — Connected apps reads and revokes OpenIddict's rows through the model rather
// than its managers, because the managers exist only where the OAuth server runs and
// these screens exist everywhere. OpenIddict's tables carry no AccountId: a farm is its
// users, so every farm-wide query and every revocation goes through AspNetUsers' AccountId.
public sealed class ConnectedApps(AppDbContext db, IUnitOfWork unitOfWork, IAuditWriter audit)
{
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
    // refuses the app next time; its tokens are revoked too, so none outlives it.
    public async Task<Result> DisconnectAsync(Guid accountId, Guid userId, string clientId, CancellationToken ct)
    {
        var subject = userId.ToString();
        var disconnected = await unitOfWork.ExecuteInTransactionAsync(async token =>
        {
            if (!await db.Users.AnyAsync(user => user.Id == userId && user.AccountId == accountId, token))
                return false;

            var authorizations = db.OAuthAuthorizations.Where(authorization => authorization.Subject == subject
                && authorization.Application!.ClientId == clientId && authorization.Status == Statuses.Valid);
            var appName = await authorizations.Select(authorization => authorization.Application!.DisplayName)
                .FirstOrDefaultAsync(token);
            var revoked = await authorizations.ExecuteUpdateAsync(
                setters => setters.SetProperty(authorization => authorization.Status, Statuses.Revoked), token);
            if (revoked == 0)
                return false;

            await db.OAuthTokens
                .Where(row => row.Subject == subject && row.Application!.ClientId == clientId
                    && (row.Status == Statuses.Valid || row.Status == Statuses.Inactive))
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, Statuses.Revoked), token);
            await audit.WriteAsync(AuditActions.UserAppDisconnected, "User", userId,
                details: new { clientId, appName }, ct: token);
            return true;
        }, ct);

        return disconnected ? Result.Success() : Result.Failure(Error.NotFound("ConnectedApp", clientId));
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
