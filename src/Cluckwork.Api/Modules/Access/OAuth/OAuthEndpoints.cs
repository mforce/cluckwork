using System.Security.Claims;
using Cluckwork.Api.Hosting;
using Cluckwork.Api.Middleware;
using Cluckwork.Api.RateLimiting;
using Cluckwork.Application.Common;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Cluckwork.Api.Modules.Access.OAuth;

public static class OAuthEndpoints
{
    // #796 — exactly the claims a session JWT carries (JwtTokenService), so the request
    // chain treats an OAuth principal like a session one: tenant, actor and roles,
    // flock scope, credential epoch and must-change-password all read these.
    private static readonly HashSet<string> SessionClaimTypes =
        [Claims.Subject, Claims.Email, "account_id", "credential_epoch", Claims.Role, "must_change_password"];

    public static RouteGroupBuilder MapOAuthEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/authorize", Authorize)
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitingOptions.OAuthAuthorizePolicyName)
            .ExcludeFromDescription();

        // The client authenticates with its code and verifier; a session bearer it happens
        // to send must not resolve a tenant and demand an Idempotency-Key.
        group.MapPost("/token", (Delegate)Token)
            .WithMetadata(new IgnoresAmbientPrincipalAttribute(), new ReadsRequestBodyAttribute())
            .RequireRateLimiting(RateLimitingOptions.OAuthTokenPolicyName)
            .WithMaxRequestBodyBytes(8192)
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

    // #796 — an endpoint opts into OAuth tokens here, and only here: the marker is
    // private, so no endpoint can accept them without the per-token rate limit and the
    // scope gate. It then accepts no session JWT. The scope check is a second
    // authorization gate beside the endpoint's role policy, so the effective permission
    // is role ∩ scope without anything computing it.
    public static TBuilder AcceptOAuthTokens<TBuilder>(this TBuilder builder, params string[] scopes)
        where TBuilder : IEndpointConventionBuilder
    {
        if (scopes.Length == 0)
            throw new ArgumentException("An endpoint that accepts OAuth tokens must name the scopes it requires.", nameof(scopes));

        return builder
            .WithMetadata(new AcceptsOAuthTokensMarker())
            .RequireRateLimiting(RateLimitingOptions.OAuthApiPolicyName)
            .RequireAuthorization(policy => policy.RequireAssertion(context =>
                scopes.Any(context.User.HasScope)));
    }

    public static bool AcceptsOAuthTokens(Endpoint? endpoint) =>
        endpoint?.Metadata.GetMetadata<AcceptsOAuthTokensMarker>() is not null;

    private sealed class AcceptsOAuthTokensMarker;

    // #795 — OpenIddict has already validated the request. No consent screen exists
    // until #798, so a signed-in caller approves its own, with the scopes it asked for.
    // OpenIddict refuses any scope it does not register, and it registers none yet.
    private static IResult Authorize(HttpContext context, ICurrentUser currentUser)
    {
        if (!currentUser.IsResolved) return Results.Unauthorized();

        var identity = new ClaimsIdentity(
            context.User.Claims.Where(claim => SessionClaimTypes.Contains(claim.Type)),
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            Claims.Name,
            Claims.Role);
        identity.SetScopes(context.GetOpenIddictServerRequest()!.GetScopes());
        identity.SetDestinations(static _ => [Destinations.AccessToken]);

        return Results.SignIn(
            new ClaimsPrincipal(identity),
            authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    // OpenIddict has validated the client, the code and its authorization before this
    // runs; the code's principal becomes the access token's.
    private static async Task<IResult> Token(HttpContext context) => Results.SignIn(
        (await context.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)).Principal!,
        authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

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
