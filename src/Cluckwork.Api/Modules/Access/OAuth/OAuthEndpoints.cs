using System.Security.Claims;
using Cluckwork.Api.Hosting;
using Cluckwork.Api.Middleware;
using Cluckwork.Api.RateLimiting;
using Cluckwork.Api.Modules.Access.Auth;
using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
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
        // #798 — anonymous so a browser navigation, which carries no bearer, reaches the
        // handler and is handed to the SPA's consent route. The SPA then calls it with its
        // session bearer to decide.
        group.MapGet("/authorize", Authorize)
            .AllowAnonymous()
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

    public const string ConsentHeaderName = "X-Cluckwork-Consent";

    // #798 — OpenIddict has already validated the client, the redirect URI, PKCE and the
    // scopes. Consent is all or nothing and needs the user's password through a step-up
    // grant, unless one of their valid permanent authorizations for this app already holds
    // every scope asked for. Then nothing new is asked and it is skipped silently.
    private static async Task<IResult> Authorize(
        HttpContext context,
        ICurrentUser currentUser,
        TenantContext tenant,
        IAccessModule access,
        IOpenIddictApplicationManager applications,
        IOpenIddictAuthorizationManager authorizations,
        [FromHeader(Name = AuthEndpoints.StepUpHeaderName)] string? stepUpToken,
        [FromHeader(Name = ConsentHeaderName)] string? consent,
        CancellationToken ct)
    {
        if (!currentUser.IsResolved)
            return context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                // An expired session: the SPA refreshes and asks again.
                ? Results.Unauthorized()
                // A browser navigation. A relative path cannot leave this origin, and the
                // query is the request OpenIddict just validated.
                : Results.Redirect("/connect" + context.Request.QueryString);

        if (consent == "deny")
            return Results.Forbid(
                new AuthenticationProperties(new Dictionary<string, string?>
                {
                    [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.AccessDenied,
                    [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "The user declined the request.",
                }),
                [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);

        var request = context.GetOpenIddictServerRequest()!;
        // RFC 6749 §3.3 lets a server default an empty request. The default is the narrower
        // scope, and consent shows it.
        var scopes = request.GetScopes() is { IsEmpty: false } asked ? asked : [OAuthScopes.ReadFarm];
        var application = (await applications.FindByClientIdAsync(request.ClientId!, ct))!;
        var applicationId = (await applications.GetIdAsync(application, ct))!;
        var subject = currentUser.UserId.ToString();

        object? covering = null;
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var authorization in authorizations.FindAsync(
            subject, applicationId, Statuses.Valid, AuthorizationTypes.Permanent, scopes: null, ct))
        {
            var granted = await authorizations.GetScopesAsync(authorization, ct);
            allowed.UnionWith(granted);
            if (covering is null && scopes.All(granted.Contains)) covering = authorization;
        }

        if (covering is null)
        {
            if (stepUpToken is null)
                return Results.Json(new ConsentRequest(
                    request.ClientId!,
                    await applications.GetDisplayNameAsync(application, ct),
                    new Uri(request.RedirectUri!).Host,
                    scopes,
                    [.. scopes.Where(allowed.Contains)]));

            var proof = await access.ConsumeStepUpGrantAsync(tenant.AccountId, currentUser.UserId, stepUpToken, ct);
            if (proof.IsFailure)
                return Results.Problem(proof.Error.Description,
                    statusCode: StatusCodes.Status403Forbidden, title: proof.Error.Code);
        }

        var identity = new ClaimsIdentity(
            context.User.Claims.Where(claim => SessionClaimTypes.Contains(claim.Type)),
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            Claims.Name,
            Claims.Role);
        identity.SetScopes(scopes);
        identity.SetDestinations(static _ => [Destinations.AccessToken]);
        // One permanent authorization per approval, reused by every later connection that
        // asks for no more. Disconnect revokes it, and with it every token it issued (#796).
        covering ??= await authorizations.CreateAsync(
            identity, subject, applicationId, AuthorizationTypes.Permanent, scopes, ct);
        identity.SetAuthorizationId(await authorizations.GetIdAsync(covering, ct));

        return Results.SignIn(
            new ClaimsPrincipal(identity),
            authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    // What the consent screen shows. The name is the app's own choice (#797).
    private sealed record ConsentRequest(
        string ClientId,
        string? ClientName,
        string RedirectHost,
        IReadOnlyList<string> Scopes,
        IReadOnlyList<string> AlreadyAllowed);

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
