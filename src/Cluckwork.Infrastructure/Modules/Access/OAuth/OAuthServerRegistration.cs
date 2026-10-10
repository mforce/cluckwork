using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Infrastructure.OAuth;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Validation;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.AspNetCore.OpenIddictServerAspNetCoreHandlers;
using static OpenIddict.Validation.OpenIddictValidationHandlers.Protection;

namespace Cluckwork.Infrastructure.Modules.Access.OAuth;

// #795 — OpenIddict as an OAuth 2.1 authorization server for third-party clients:
// authorization code with PKCE, reference access tokens that live until revoked (#788).
public static class OAuthServerRegistration
{
    // #806 — the one resource this server issues tokens for: the API's /mcp endpoint,
    // under the configured issuer and never the request's Host (#538). RFC 8707 binds
    // each token to it, so a token cannot be replayed at any other resource.
    public static Uri ProtectedResource(Uri issuer) => new(PublicBase(issuer), "mcp");

    public static IServiceCollection AddAccessOAuthServer(
        this IServiceCollection services, Uri issuer, bool allowPlainHttp, ClientMetadataOptions clientMetadata)
    {
        var resource = ProtectedResource(issuer).AbsoluteUri;
        services.AddOpenIddict()
            .AddCore(core => core
                .UseEntityFrameworkCore()
                .UseDbContext<AppDbContext>()
                .ReplaceDefaultEntities<Guid>())
            .AddServer(server =>
            {
                server.SetIssuer(issuer)
                    .SetAuthorizationEndpointUris("api/v1/oauth/authorize")
                    .SetTokenEndpointUris("api/v1/oauth/token")
                    .SetJsonWebKeySetEndpointUris(Array.Empty<Uri>())
                    .AllowAuthorizationCodeFlow()
                    .RequireProofKeyForCodeExchange()
                    .UseReferenceAccessTokens()
                    .SetAccessTokenLifetime(null)
                    .RegisterScopes([.. OAuthScopes.All])
                    // A request naming any other resource is refused (invalid_target).
                    .RegisterResources(resource)
                    // Every client registers itself anonymously (#797), so a per-client
                    // scope or resource permission would be granted to all of them anyway.
                    // The user's consent is what limits a connection's scopes (#798).
                    .IgnoreScopePermissions()
                    .IgnoreResourcePermissions()
                    // OpenIddict refuses to start without both keys, but with the Data
                    // Protection format they only ever sign identity tokens, which this
                    // server never issues. Codes and access tokens are protected by the
                    // shared key ring (#794), so ephemeral per-process keys are enough.
                    .AddEphemeralEncryptionKey()
                    .AddEphemeralSigningKey()
                    .UseDataProtection();
                server.Configure(options =>
                {
                    // openid is registered by default and granted implicitly, which would
                    // mint an identity token signed with a key no other replica holds.
                    options.Scopes.Remove(Scopes.OpenId);
                    // A plain challenge is the verifier itself, so it protects nothing once
                    // the authorization request leaks. OAuth 2.1 clients send S256.
                    options.CodeChallengeMethods.Remove(CodeChallengeMethods.Plain);
                    // The consent route hands the client's redirect to the SPA as a URL
                    // (below), which only a query-mode response is.
                    options.ResponseModes.Remove(ResponseModes.FormPost);
                    options.ResponseModes.Remove(ResponseModes.Fragment);
                });

                // #798 — the SPA's consent route asks with its session bearer through fetch,
                // which cannot read a cross-origin redirect. It gets the client's redirect as
                // JSON and navigates there itself. A browser navigation carries no bearer and
                // keeps the standard redirect.
                server.AddEventHandler<OpenIddictServerEvents.ApplyAuthorizationResponseContext>(handler => handler
                    .UseInlineHandler(static async context =>
                    {
                        var request = context.Transaction.GetHttpRequest()!;
                        if (context.RedirectUri is null || !HasBearer(request))
                            return;

                        var location = context.RedirectUri;
                        foreach (var (name, value) in context.Response.GetParameters())
                            foreach (var item in (StringValues)value)
                                if (!string.IsNullOrEmpty(item))
                                    location = QueryHelpers.AddQueryString(location, name, item);

                        // OpenIddict has already set 400 for an error, which the client
                        // still receives through the redirect.
                        var response = request.HttpContext.Response;
                        response.StatusCode = StatusCodes.Status200OK;
                        response.Headers.CacheControl = "no-store";
                        await response.WriteAsJsonAsync(new { redirectUri = location }, request.HttpContext.RequestAborted);
                        context.HandleRequest();
                    })
                    .SetOrder(Authentication.ProcessQueryResponse.Descriptor.Order - 1));

                // OpenIddict also accepts a POSTed authorization request, which matches no
                // endpoint and so no rate-limit policy or body cap. Refused before the body
                // is read or a client looked up (#796).
                server.AddEventHandler<OpenIddictServerEvents.ExtractAuthorizationRequestContext>(handler => handler
                    .UseInlineHandler(static context =>
                    {
                        if (!HttpMethods.IsGet(context.Transaction.GetHttpRequest()!.Method))
                            context.Reject(Errors.InvalidRequest, "Authorization requests must use GET.");
                        return default;
                    })
                    .SetOrder(ExtractGetOrPostRequest<OpenIddictServerEvents.ExtractAuthorizationRequestContext>.Descriptor.Order - 1));

                // #798 — discovery names endpoints under the configured issuer, not under the
                // Host the request arrived with: behind a proxy that Host can be an internal
                // name. #797 — OpenIddict has no registration endpoint; the API maps one
                // beside authorize, and discovery points clients at it.
                var publicBase = PublicBase(issuer);
                server.AddEventHandler<OpenIddictServerEvents.HandleConfigurationRequestContext>(handler => handler
                    .UseInlineHandler(context =>
                    {
                        context.AuthorizationEndpoint = new Uri(publicBase, context.BaseUri!.MakeRelativeUri(context.AuthorizationEndpoint!));
                        context.TokenEndpoint = new Uri(publicBase, context.BaseUri!.MakeRelativeUri(context.TokenEndpoint!));
                        context.Metadata["registration_endpoint"] =
                            new Uri(context.AuthorizationEndpoint, "register").AbsoluteUri;
                        // #1148 — a client may name its metadata document as its client_id.
                        if (clientMetadata.Enabled)
                            context.Metadata["client_id_metadata_document_supported"] = true;
                        return default;
                    })
                    .SetOrder(OpenIddictServerHandlers.Discovery.AttachEndpoints.Descriptor.Order + 1));

                server.AddEventHandler(ClientMetadataDocuments.Descriptor);

                // #806 — RFC 8707: the code, and the token it becomes, carry the resource the
                // client named, which OpenIddict has already checked is /mcp. A client that
                // names none gets /mcp, the only resource there is.
                server.AddEventHandler<OpenIddictServerEvents.ProcessSignInContext>(handler => handler
                    .UseInlineHandler(context =>
                    {
                        if (context.EndpointType is OpenIddictServerEndpointType.Authorization)
                            context.Principal!.SetResources(context.Request!.GetResources() is { IsEmpty: false } asked
                                ? asked
                                : [resource]);
                        return default;
                    })
                    .SetOrder(OpenIddictServerHandlers.InferResources.Descriptor.Order - 500));

                // Token passthrough maps the endpoint, which is what lets it opt into a
                // rate-limit policy and a body cap; OpenIddict still validates first.
                var aspNetCore = server.UseAspNetCore()
                    .EnableAuthorizationEndpointPassthrough()
                    .EnableTokenEndpointPassthrough();
                if (allowPlainHttp)
                    aspNetCore.DisableTransportSecurityRequirement();
            })
            .AddValidation(validation =>
            {
                validation.UseLocalServer();
                validation.UseDataProtection();
                // #806 — a token without this audience, or issued for another resource,
                // is refused.
                validation.AddAudiences(resource);
                // Disconnect revokes the authorization, so every request checks it (#796).
                validation.EnableAuthorizationEntryValidation();
                // OpenIddict skips that check for a token that names no authorization, so
                // such a token is refused rather than trusted.
                validation.AddEventHandler<OpenIddictValidationEvents.ValidateTokenContext>(handler => handler
                    .UseInlineHandler(static context =>
                    {
                        if (string.IsNullOrEmpty(context.AuthorizationId))
                            context.Reject(Errors.InvalidToken, "The token is not bound to an authorization.");
                        return default;
                    })
                    .SetOrder(ValidateAuthorizationEntry.Descriptor.Order + 1_000));
                validation.AddEventHandler<OpenIddictValidationEvents.ValidateTokenContext>(handler => handler
                    .UseScopedHandler<OAuthLastUsedStamp>()
                    .SetOrder(ValidateAuthorizationEntry.Descriptor.Order + 2_000));
                // Header only: a token in a query string reaches request logs, and one in a
                // form body would dodge the per-token rate-limit key (RateLimitKey.ForBearer).
                validation.UseAspNetCore()
                    .DisableAccessTokenExtractionFromBodyForm()
                    .DisableAccessTokenExtractionFromQueryString();
            });

        services.AddScoped<IOAuthPurge, OAuthPurge>();
        services.AddSingleton(clientMetadata);
        services.AddSingleton(_ => new ClientMetadataFetcher(
            FetchTransport.System, clientMetadata.PrivateHosts ?? new HashSet<string>()));

        return services;
    }

    private static Uri PublicBase(Uri issuer) =>
        issuer.AbsoluteUri.EndsWith('/') ? issuer : new Uri(issuer.AbsoluteUri + "/");

    private static bool HasBearer(HttpRequest request) =>
        request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
}
