using System.Security.Claims;
using Cluckwork.Application.Common;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Cluckwork.Api.Modules.Access.OAuth;

// #795 — OpenIddict validates the authorization request before it reaches this
// endpoint; the endpoint only decides who is approving it.
public static class OAuthEndpoints
{
    public static RouteGroupBuilder MapOAuthEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/authorize", Authorize)
            .RequireAuthorization()
            .ExcludeFromDescription();

        return group;
    }

    // No consent screen exists until #798, so a signed-in caller approves its own
    // request. The token names the user and nothing else: without account_id and
    // credential_epoch, CredentialEpochMiddleware rejects it on every route until
    // #796 decides what an OAuth principal carries.
    private static IResult Authorize(ICurrentUser currentUser)
    {
        if (!currentUser.IsResolved) return Results.Unauthorized();

        var identity = new ClaimsIdentity(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        identity.SetClaim(Claims.Subject, currentUser.UserId.ToString());
        identity.SetDestinations(static _ => [Destinations.AccessToken]);

        return Results.SignIn(
            new ClaimsPrincipal(identity),
            authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }
}
