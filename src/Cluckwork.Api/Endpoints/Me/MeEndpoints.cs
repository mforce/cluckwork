namespace Cluckwork.Api.Endpoints.Me;

using Cluckwork.Application.Common;
using Cluckwork.Infrastructure.Persistence;

// #45 — the user-scoped counterpart to the farm-scoped /account group. Identity
// comes from the JWT (sub), not the body. Mounted on the DEFAULT auth policy so
// every role, ReadOnly included, can read their identity and set their language.
public static class MeEndpoints
{
    public static RouteGroupBuilder MapMeEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", GetMe)
            .WithName("GetMe")
            .WithSummary("The signed-in user's identity and UI-language preference.");

        return group;
    }

    private static async Task<IResult> GetMe(
        IIdentityProvider identity, ICurrentUser currentUser, TenantContext tenant,
        CancellationToken ct)
    {
        if (!currentUser.IsResolved || !tenant.IsResolved) return Results.Unauthorized();
        var profile = await identity.GetUserAsync(tenant.AccountId, currentUser.UserId, ct);
        return profile is null
            ? Results.NotFound()
            : Results.Ok(new MeResponse(
                profile.Id, profile.Email, profile.DisplayName, profile.Role, profile.Language));
    }
}

// Field order is part of the contract the SPA (#182) parses. `Name` is DisplayName
// so the SPA needn't decode the JWT for it; role is echoed (JWT stays authoritative).
public sealed record MeResponse(Guid Id, string Email, string? Name, string Role, string? Language);
