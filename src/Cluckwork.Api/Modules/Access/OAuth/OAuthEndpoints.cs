using System.Security.Claims;
using Cluckwork.Api.Hosting;
using Cluckwork.Api.Middleware;
using Cluckwork.Api.RateLimiting;
using Cluckwork.Api.Modules.Access.Auth;
using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Application.Modules.Farm.Contracts;
using Cluckwork.Infrastructure.OAuth;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Cluckwork.Api.Modules.Access.OAuth;

public static class OAuthEndpoints
{
    // #800 — the client's display name, carried in its access tokens beside client_id.
    public const string ClientNameClaim = "client_name";

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
            .RequireAuthorization(policy => policy.AddRequirements(new OAuthScopeRequirement(scopes)));
    }

    public static bool AcceptsOAuthTokens(Endpoint? endpoint) =>
        endpoint?.Metadata.GetMetadata<AcceptsOAuthTokensMarker>() is not null;

    private sealed class AcceptsOAuthTokensMarker;

    public const string ConsentHeaderName = "X-Cluckwork-Consent";

    // #1326 — the scopes the person ticked, as a JSON array.
    public const string ConsentScopesHeaderName = "X-Cluckwork-Consent-Scopes";

    // #1146 — the farm's switch is off: consent and the per-request check both say so.
    public const string ConnectedAppsOff = "Auth.ConnectedAppsOff";

    // #798 — OpenIddict has already validated the client, the redirect URI, PKCE and the
    // scopes. The person grants the requested scopes they tick (#1326), and every approval
    // needs their password through a step-up grant.
    private static async Task<IResult> Authorize(
        HttpContext context,
        ICurrentUser currentUser,
        TenantContext tenant,
        FlockScope flockScope,
        IAccessModule access,
        IFarmModule farm,
        IOpenIddictApplicationManager applications,
        IOpenIddictAuthorizationManager authorizations,
        IUnitOfWork unitOfWork,
        IAuditWriter audit,
        [FromHeader(Name = AuthEndpoints.StepUpHeaderName)] string? stepUpToken,
        [FromHeader(Name = ConsentHeaderName)] string? consent,
        CancellationToken ct)
    {
        if (!currentUser.IsResolved)
        {
            // An expired session: the SPA refreshes and asks again.
            if (context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return Results.Unauthorized();
            // The sign-in screen names the app it continues to. The name is the app's
            // own public choice (#797), and its verified domain is its client_id's host
            // (#1148), so neither needs a session.
            if (consent == "preview")
            {
                var clientId = context.GetOpenIddictServerRequest()!.ClientId!;
                return Results.Json(new
                {
                    clientName = await applications.GetDisplayNameAsync(
                        (await applications.FindByClientIdAsync(clientId, ct))!, ct),
                    verifiedDomain = ClientMetadata.VerifiedDomain(clientId),
                });
            }
            // A browser navigation. A relative path cannot leave this origin, and the
            // query is the request OpenIddict just validated.
            return Results.Redirect("/connect" + context.Request.QueryString);
        }

        if (consent == "deny")
            return DenyConsent();

        var request = context.GetOpenIddictServerRequest()!;
        // RFC 6749 §3.3 lets a server default an empty request. The default is the narrower
        // scope, and consent shows it.
        var requestedScopes = request.GetScopes() is { IsEmpty: false } asked ? asked : [OAuthScopes.ReadFarm];
        var usable = await CluckworkMcp.UsableScopesAsync(context.RequestServices, context.User);
        // #1326 — the person's choice, checked before the farm switch and before any password
        // is spent. Nothing chosen is Cancel; a request carrying neither a choice nor a
        // password is the first load of the consent screen.
        var choice = context.Request.Headers[ConsentScopesHeaderName];
        var selection = ConsentSelection.Read(requestedScopes, usable, choice);
        if (selection.IsFailure)
            return Results.Problem(selection.Error.Description,
                statusCode: StatusCodes.Status400BadRequest, title: selection.Error.Code);
        var grantedScopes = selection.Value.Scopes;
        if (grantedScopes.IsEmpty && (choice.Count > 0 || stepUpToken is not null))
            return DenyConsent();

        // #1146 — before any password is asked or code issued. Cancel, above, still
        // sends the user back to the app.
        if ((await farm.GetSettingsAsync(ct))?.AllowConnectedApps != true)
            return Results.Problem("This farm doesn't allow connected apps. Ask an Owner.",
                statusCode: StatusCodes.Status403Forbidden, title: ConnectedAppsOff);

        var application = (await applications.FindByClientIdAsync(request.ClientId!, ct))!;
        var applicationId = (await applications.GetIdAsync(application, ct))!;
        var subject = currentUser.UserId.ToString();

        // Approval reuses only an authorization holding exactly the chosen scopes, so the
        // stored row always equals the token's scopes (#1326). Whether one row already covers
        // the request is history for the screen's headline, never a reason to skip anything.
        object? reusable = null;
        var requestedCovered = false;
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var authorization in authorizations.FindAsync(
            subject, applicationId, Statuses.Valid, AuthorizationTypes.Permanent, scopes: null, ct))
        {
            var authorizationScopes = await authorizations.GetScopesAsync(authorization, ct);
            allowed.UnionWith(authorizationScopes);
            requestedCovered |= requestedScopes.All(authorizationScopes.Contains);
            if (reusable is null && authorizationScopes.ToHashSet(StringComparer.Ordinal).SetEquals(grantedScopes))
                reusable = authorization;
        }

        // Every code costs a spent step-up grant, a reconnect included: the code becomes a
        // token that never expires, so a session bearer alone must never mint one.
        if (stepUpToken is null)
            return Results.Json(new ConsentRequest(
                request.ClientId!,
                await applications.GetDisplayNameAsync(application, ct),
                ClientMetadata.VerifiedDomain(request.ClientId),
                new Uri(ValidatedRedirectUri(context)).Host,
                requestedScopes,
                [.. requestedScopes.Where(allowed.Contains)],
                [.. requestedScopes.Where(usable.Contains)],
                AlreadyApproved: requestedCovered,
                AssignedFlocks: flockScope.IsUnrestricted ? null
                    : [.. (await access.ListFlockAssignmentsAsync(currentUser.UserId, ct))
                        .Select(assignment => assignment.FlockName).OfType<string>()]));

        var proof = await access.ConsumeStepUpGrantAsync(tenant.AccountId, currentUser.UserId, stepUpToken, ct);
        if (proof.IsFailure)
            return Results.Problem(proof.Error.Description,
                statusCode: StatusCodes.Status403Forbidden, title: proof.Error.Code);

        var identity = new ClaimsIdentity(
            context.User.Claims.Where(claim => SessionClaimTypes.Contains(claim.Type)),
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            Claims.Name,
            Claims.Role);
        identity.SetScopes(grantedScopes);
        // #800 — the name rides in the token, as the user's email does, so an audit row
        // snapshots it without a lookup per write. Registration never renames a client.
        var appName = await applications.GetDisplayNameAsync(application, ct);
        identity.SetClaim(ClientNameClaim, appName);
        identity.SetDestinations(static _ => [Destinations.AccessToken]);
        // One permanent authorization per distinct granted set, reused by every later
        // approval of exactly that set. Disconnect revokes them all, and with them every
        // token they issued (#796). A narrower approval leaves earlier ones as they are.
        // #799 — the approval and its audit row commit together. The person acted here
        // themselves, so the row's connected-app columns stay empty; details name the app.
        var reconnect = reusable is not null;
        await unitOfWork.ExecuteInTransactionAsync(async token =>
        {
            reusable ??= await authorizations.CreateAsync(
                identity, subject, applicationId, AuthorizationTypes.Permanent, grantedScopes, token);
            await audit.WriteAsync(reconnect ? AuditActions.UserAppReconnected : AuditActions.UserAppConnected,
                "User", currentUser.UserId,
                details: new { clientId = request.ClientId, appName, scopes = grantedScopes, requested = requestedScopes },
                ct: token);
            return true;
        }, ct);
        identity.SetAuthorizationId(await authorizations.GetIdAsync(reusable!, ct));

        return Results.SignIn(
            new ClaimsPrincipal(identity),
            authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    // Cancel, and an empty choice: the client gets access_denied, and nothing is spent or
    // recorded.
    private static IResult DenyConsent() => Results.Forbid(
        new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.AccessDenied,
            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "The user declined the request.",
        }),
        [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);

    // What the consent screen shows. The name is the app's own choice (#797). A client
    // whose id is its metadata document's URL has a VerifiedDomain, the host Cluckwork
    // fetched that document from (#1148); for any other client it is null. Usable is the
    // requested scopes this person's role can use (#1326); the others show as not allowed.
    // AlreadyApproved means one earlier approval holds every requested scope; it only picks
    // the headline. AssignedFlocks is the user's flock scope as this request resolved it:
    // null for every flock, else the names of the flocks they are assigned to.
    private sealed record ConsentRequest(
        string ClientId,
        string? ClientName,
        string? VerifiedDomain,
        string RedirectHost,
        IReadOnlyList<string> Scopes,
        IReadOnlyList<string> AlreadyAllowed,
        IReadOnlyList<string> Usable,
        bool AlreadyApproved,
        IReadOnlyList<string>? AssignedFlocks);

    // A client with one registered redirect URI may omit redirect_uri; OpenIddict then
    // validates that one and keeps it on the validation context, not on the request.
    private static string ValidatedRedirectUri(HttpContext context) =>
        context.Features.Get<OpenIddictServerAspNetCoreFeature>()!.Transaction!
            .GetProperty<OpenIddictServerEvents.ValidateAuthorizationRequestContext>(
                typeof(OpenIddictServerEvents.ValidateAuthorizationRequestContext).FullName!)!
            .RedirectUri!;

    // OpenIddict has validated the client, the code and its authorization before this
    // runs; the code's principal becomes the access token's.
    private static async Task<IResult> Token(HttpContext context) => Results.SignIn(
        (await context.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)).Principal!,
        authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

    // RFC 7591 §3.2: 201 with the registered metadata, or 400 with an OAuth error code.
    private static async Task<IResult> Register(
        ClientRegistrationRequest request, IOpenIddictApplicationManager applications, CancellationToken ct)
    {
        var registration = ClientMetadata.ToDescriptor(request, Guid.NewGuid().ToString("N"));
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
