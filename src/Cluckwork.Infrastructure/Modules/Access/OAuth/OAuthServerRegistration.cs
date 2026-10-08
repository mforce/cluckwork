using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
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
    public static IServiceCollection AddAccessOAuthServer(
        this IServiceCollection services, Uri issuer, bool allowPlainHttp)
    {
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
                });

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
                // Header only: a token in a query string reaches request logs, and one in a
                // form body would dodge the per-token rate-limit key (RateLimitKey.ForBearer).
                validation.UseAspNetCore()
                    .DisableAccessTokenExtractionFromBodyForm()
                    .DisableAccessTokenExtractionFromQueryString();
            });


        return services;
    }
}
