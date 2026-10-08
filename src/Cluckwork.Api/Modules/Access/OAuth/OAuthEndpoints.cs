using System.Security.Claims;
using Cluckwork.Api.Hosting;
using Cluckwork.Api.Middleware;
using Cluckwork.Api.RateLimiting;
using Cluckwork.Application.Common;
using Microsoft.AspNetCore;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Cluckwork.Api.Modules.Access.OAuth;

public static class OAuthEndpoints
{
    // #800 — the client's display name, carried in its access tokens beside client_id.
    public const string ClientNameClaim = "client_name";

    public static RouteGroupBuilder MapOAuthEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/authorize", Authorize)
            .RequireAuthorization()
            .ExcludeFromDescription();

        // #797 — open to anyone on purpose. A registered client gets no access: it can
        // only send a user to the consent screen (#798), and nothing is issued until that
        // user approves with their password. What an anonymous caller can do here is add
        // rows, so registration is rate-limited per client IP across replicas, and an app
        // nobody approves is deleted after OAuthPurgeSweep.UnapprovedWindow.
        group.MapPost("/register", Register)
            .AllowAnonymous()
            .WithMetadata(new IgnoresAmbientPrincipalAttribute())
            .RequireRateLimiting(RateLimitingOptions.OAuthRegisterPolicyName)
            .WithMaxRequestBodyBytes(16 * 1024)
            .ExcludeFromDescription();

        return group;
    }

    // #795 — OpenIddict has already validated the request. No consent screen exists
    // until #798, so a signed-in caller approves its own. The token names the user and
    // nothing else until #796 decides what an OAuth principal carries.
    private static async Task<IResult> Authorize(
        HttpContext context, ICurrentUser currentUser, IOpenIddictApplicationManager applications,
        CancellationToken ct)
    {
        if (!currentUser.IsResolved) return Results.Unauthorized();

        var identity = new ClaimsIdentity(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        identity.SetClaim(Claims.Subject, currentUser.UserId.ToString());
        // #800 — the name rides in the token, as the user's email does, so the audit row
        // snapshots it without a lookup per write. Registration never renames a client.
        var application = await applications.FindByClientIdAsync(context.GetOpenIddictServerRequest()!.ClientId!, ct);
        identity.SetClaim(ClientNameClaim,
            application is null ? null : await applications.GetDisplayNameAsync(application, ct));
        identity.SetDestinations(static _ => [Destinations.AccessToken]);

        return Results.SignIn(
            new ClaimsPrincipal(identity),
            authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    // RFC 7591 §3.2: 201 with the registered metadata, or 400 with an OAuth error code.
    private static async Task<IResult> Register(
        ClientRegistrationRequest request, IOpenIddictApplicationManager applications, CancellationToken ct)
    {
        var registration = ClientRegistration.ToDescriptor(request);
        if (registration.IsFailure)
            return RegistrationError(registration.Error.Code, registration.Error.Description);

        var client = registration.Value;
        try
        {
            await applications.CreateAsync(client, ct);
        }
        catch (OpenIddictExceptions.ValidationException exception)
        {
            // The redirect URIs are the only client input OpenIddict validates here; it
            // refuses, for one, an iss parameter in their query (issuer fixation).
            return RegistrationError("invalid_redirect_uri",
                string.Join(" ", exception.Results.Select(result => result.ErrorMessage)));
        }

        return Results.Json(new
        {
            client_id = client.ClientId,
            client_id_issued_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            client_name = client.DisplayName,
            // What OpenIddict stored and compares ordinally, so the client can use it as is.
            redirect_uris = client.RedirectUris.Select(uri => uri.OriginalString),
            grant_types = new[] { GrantTypes.AuthorizationCode },
            response_types = new[] { ResponseTypes.Code },
            token_endpoint_auth_method = ClientAuthenticationMethods.None,
        }, statusCode: StatusCodes.Status201Created);
    }

    private static IResult RegistrationError(string error, string description) =>
        Results.Json(new { error, error_description = description }, statusCode: StatusCodes.Status400BadRequest);
}
