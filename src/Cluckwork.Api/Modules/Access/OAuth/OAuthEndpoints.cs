using System.Security.Claims;
using Cluckwork.Application.Common;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Cluckwork.Api.Modules.Access.OAuth;

public static class OAuthEndpoints
{
    public static RouteGroupBuilder MapOAuthEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/authorize", Authorize)
            .RequireAuthorization()
            .ExcludeFromDescription();

        return group;
    }

    // #795 — OpenIddict has already validated the request. No consent screen exists
    // until #798, so a signed-in caller approves its own. The token names the user and
    // nothing else until #796 decides what an OAuth principal carries.
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
