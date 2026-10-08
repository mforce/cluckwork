using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using Cluckwork.Domain.Common;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Cluckwork.Api.Modules.Access.OAuth;

// RFC 7591 §2 client metadata. Fields this server does not honour (logo_uri, client_uri,
// contacts, ...) are not bound, so nothing a client says about itself is stored or echoed
// beyond its name and redirect URIs.
public sealed record ClientRegistrationRequest(
    [property: JsonPropertyName("client_name")] string? ClientName,
    [property: JsonPropertyName("redirect_uris")] string?[]? RedirectUris,
    [property: JsonPropertyName("grant_types")] string?[]? GrantTypes,
    [property: JsonPropertyName("response_types")] string?[]? ResponseTypes,
    [property: JsonPropertyName("token_endpoint_auth_method")] string? TokenEndpointAuthMethod);

// #797 — what a self-registered client may be: a public client using the authorization
// code with PKCE (S256 is server-wide, #795), redirecting only to loopback or https.
internal static class ClientRegistration
{
    private const int MaxNameLength = 100;
    private const int MaxRedirectUris = 10;
    private const int MaxRedirectUriLength = 2048;

    public static Result<OpenIddictApplicationDescriptor> ToDescriptor(ClientRegistrationRequest request)
    {
        if (request.RedirectUris is not { Length: > 0 and <= MaxRedirectUris } redirects)
            return InvalidRedirect($"redirect_uris must list between 1 and {MaxRedirectUris} URIs.");

        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = Guid.NewGuid().ToString("N"),
            ClientType = ClientTypes.Public,
            DisplayName = SanitizeName(request.ClientName),
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.Token,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.ResponseTypes.Code,
            },
        };

        foreach (var redirect in redirects)
        {
            if (!IsAllowedRedirect(redirect, out var uri))
                return InvalidRedirect(
                    "Each redirect URI must be https, or http on 127.0.0.1 or [::1], with no fragment or user info.");
            descriptor.RedirectUris.Add(uri);
        }

        // refresh_token is accepted and dropped: access tokens last until revoked (#788),
        // and RFC 7591 §3.2.1 lets the server register less than the client asked for.
        // MCP clients routinely ask for it, so refusing it would refuse them.
        if (request.GrantTypes is { } grants
            && (!grants.Contains(GrantTypes.AuthorizationCode)
                || grants.Any(grant => grant is not (GrantTypes.AuthorizationCode or GrantTypes.RefreshToken))))
            return InvalidMetadata("grant_types must be authorization_code.");

        if (request.ResponseTypes is { } responses && responses.Any(type => type != ResponseTypes.Code))
            return InvalidMetadata("response_types must be code.");

        if (request.TokenEndpointAuthMethod is not (null or ClientAuthenticationMethods.None))
            return InvalidMetadata("token_endpoint_auth_method must be none; this server registers public clients only.");

        return descriptor;
    }

    private static bool IsAllowedRedirect(string? value, [NotNullWhen(true)] out Uri? uri) =>
        Uri.TryCreate(value, UriKind.Absolute, out uri)
        && value.Length <= MaxRedirectUriLength
        && uri.Fragment.Length == 0
        && uri.UserInfo.Length == 0
        && (uri.Scheme == Uri.UriSchemeHttps && uri.Host.Length != 0
            || uri.Scheme == Uri.UriSchemeHttp && uri.Host is "127.0.0.1" or "[::1]");

    // The consent screen (#798) shows this name, and the client chose it. Controls and
    // format characters (bidi overrides and isolates among them) become spaces so a name
    // cannot reorder or hide the text around it; the cap counts UTF-16 units and cuts on a
    // grapheme boundary, so a stack of combining marks cannot exceed it either.
    private static string? SanitizeName(string? name)
    {
        if (name is null)
            return null;

        var spaced = new StringBuilder(name.Length);
        foreach (var rune in name.Normalize(NormalizationForm.FormC).EnumerateRunes())
        {
            var hidden = Rune.IsWhiteSpace(rune) || Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control
                or UnicodeCategory.Format or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned;
            if (hidden)
            {
                if (spaced.Length != 0 && spaced[^1] != ' ')
                    spaced.Append(' ');
            }
            else
            {
                spaced.Append(rune.ToString());
            }
        }

        var text = spaced.ToString().TrimEnd();
        var capped = new StringBuilder(Math.Min(text.Length, MaxNameLength));
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext() && capped.Length + elements.GetTextElement().Length <= MaxNameLength)
            capped.Append(elements.GetTextElement());

        var result = capped.ToString().TrimEnd();
        return result.Length == 0 ? null : result;
    }

    private static Result<OpenIddictApplicationDescriptor> InvalidRedirect(string description) =>
        Result.Failure<OpenIddictApplicationDescriptor>(new Error("invalid_redirect_uri", description));

    private static Result<OpenIddictApplicationDescriptor> InvalidMetadata(string description) =>
        Result.Failure<OpenIddictApplicationDescriptor>(new Error("invalid_client_metadata", description));
}
