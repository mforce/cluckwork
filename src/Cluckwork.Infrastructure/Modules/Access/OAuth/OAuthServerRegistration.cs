using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;

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

                // #797 — OpenIddict has no registration endpoint; the API maps one beside
                // authorize, and discovery points clients at it.
                server.AddEventHandler<OpenIddictServerEvents.HandleConfigurationRequestContext>(handler => handler
                    .UseInlineHandler(context =>
                    {
                        context.Metadata[RegistrationEndpointMetadata] =
                            new Uri(context.AuthorizationEndpoint!, "register").AbsoluteUri;
                        return default;
                    })
                    .SetOrder(OpenIddictServerHandlers.Discovery.AttachEndpoints.Descriptor.Order + 1));

                var aspNetCore = server.UseAspNetCore().EnableAuthorizationEndpointPassthrough();
                if (allowPlainHttp)
                    aspNetCore.DisableTransportSecurityRequirement();
            })
            .AddValidation(validation =>
            {
                validation.UseLocalServer();
                validation.UseDataProtection();
                validation.UseAspNetCore();
            });

        services.AddScoped<IOAuthPurge, OAuthPurge>();
        return services;
    }

    private const string RegistrationEndpointMetadata = "registration_endpoint";
}
