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
}
