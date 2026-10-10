using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cluckwork.Domain.Auditing;
using Cluckwork.Domain.Common;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Cluckwork.Infrastructure.OAuth;

// RFC 7591 §2 client metadata. Fields this server does not honour (logo_uri, client_uri,
// contacts, ...) are not bound, so nothing a client says about itself is stored or echoed
// beyond its name and redirect URIs.
public sealed record ClientRegistrationRequest(
    [property: JsonPropertyName("client_name")] string? ClientName,
    [property: JsonPropertyName("redirect_uris")] string?[]? RedirectUris,
    [property: JsonPropertyName("grant_types")] string?[]? GrantTypes,
    [property: JsonPropertyName("response_types")] string?[]? ResponseTypes,
    [property: JsonPropertyName("token_endpoint_auth_method")] string? TokenEndpointAuthMethod);

// #797 — what a client may be, whether it registered itself (DCR) or publishes a client
// ID metadata document (#1148): a public client using the authorization code with PKCE
// (S256 is server-wide, #795), redirecting only to loopback or https.
public static class ClientMetadata
{
    private const int MaxNameLength = 100;
    private const int MaxRedirectUris = 10;
    private const int MaxRedirectUriLength = 2048;

    // OpenIddict stores a client id in 100 characters, and so do the audit rows that name a
    // connected app (#800). Known metadata document URLs are far shorter.
    public const int MaxDocumentUrlLength = AuditEvent.MaxConnectedAppClientIdLength;

    // DCR mints Guid "N" ids, so the prefix alone tells the two kinds apart (draft §7.1).
    public static bool IsDocumentUrl([NotNullWhen(true)] string? clientId) =>
        clientId is not null && clientId.StartsWith("https://", StringComparison.Ordinal);

    // The draft compares client ids as plain strings, so only one spelling of each URL is
    // accepted: the one .NET prints back unchanged. That one clause refuses dot segments,
    // an upper-case host and an explicit :443. ASCII without '%' and a DNS host leave no
    // encoding or IP-literal form for the address check to disagree with.
    public static Result<Uri> ParseDocumentUrl(string clientId)
    {
        if (clientId.Length > MaxDocumentUrlLength)
            return InvalidUrl($"A metadata document URL may have at most {MaxDocumentUrlLength} characters.");
        if (!Ascii.IsValid(clientId) || clientId.Contains('%')
            || !Uri.TryCreate(clientId, UriKind.Absolute, out var url)
            || url.Scheme != Uri.UriSchemeHttps || url.HostNameType != UriHostNameType.Dns
            || url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0
            || url.AbsolutePath == "/" || url.AbsoluteUri != clientId)
            return InvalidUrl("A metadata document URL must be a plain https URL with a host name and a path, and no user info, query or fragment.");
        return url;
    }

    // What the consent screen may call checked: the server fetched the client's details
    // from this host itself. The client's name is still its own claim.
    public static string? VerifiedDomain(string? clientId) =>
        IsDocumentUrl(clientId) ? new Uri(clientId).Authority : null;

    // Every field is untrusted. The document must name itself (draft §4) and must not
    // carry a secret (§4.1); the rest is held to exactly the rules a DCR request meets.
    public static Result<OpenIddictApplicationDescriptor> FromDocument(string documentUrl, ReadOnlyMemory<byte> json)
    {
        ClientRegistrationRequest? request;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowDuplicateProperties = false, MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("client_id", out var clientId)
                || clientId.ValueKind != JsonValueKind.String
                || clientId.GetString() != documentUrl)
                return InvalidMetadata("The document's client_id must equal its URL.");
            if (root.TryGetProperty("client_secret", out _) || root.TryGetProperty("client_secret_expires_at", out _))
                return InvalidMetadata("A metadata document must not carry a client secret.");
            request = root.Deserialize<ClientRegistrationRequest>();
        }
        // GetString and Deserialize throw InvalidOperationException, not JsonException, for
        // a string holding a lone surrogate.
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return InvalidMetadata("The metadata document is not valid client metadata.");
        }

        return ToDescriptor(request!, documentUrl);
    }

    public static Result<OpenIddictApplicationDescriptor> ToDescriptor(ClientRegistrationRequest request, string clientId)
    {
        if (request.RedirectUris is not { Length: > 0 and <= MaxRedirectUris } redirects)
            return InvalidRedirect($"redirect_uris must list between 1 and {MaxRedirectUris} URIs.");

        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
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

        var uris = new List<Uri>(redirects.Length);
        foreach (var redirect in redirects)
        {
            if (!IsAllowedRedirect(redirect, out var uri))
                return InvalidRedirect(
                    "Each redirect URI must be https, or http on localhost, 127.0.0.1 or [::1], with no fragment or user info.");
            uris.Add(uri);
        }

        // RFC 8252 §7.3: a native app listens on whichever loopback port it gets, so its
        // authorization request may name any port. OpenIddict allows that only for a
        // native application whose stored loopback URI has no port; scheme, host, path
        // and query must still match. A client that also lists an https URI stays a web
        // client and matches every URI exactly.
        var native = uris.All(IsLoopback);
        if (native)
            descriptor.ApplicationType = ApplicationTypes.Native;
        foreach (var uri in uris)
            descriptor.RedirectUris.Add(native ? new UriBuilder(uri) { Port = -1 }.Uri : uri);

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
            return InvalidMetadata("token_endpoint_auth_method must be none; this server accepts public clients only.");

        return descriptor;
    }

    private static bool IsAllowedRedirect(string? value, [NotNullWhen(true)] out Uri? uri) =>
        Uri.TryCreate(value, UriKind.Absolute, out uri)
        && value.Length <= MaxRedirectUriLength
        && uri.Fragment.Length == 0
        && uri.UserInfo.Length == 0
        && (uri.Scheme == Uri.UriSchemeHttps && uri.Host.Length != 0 || IsLoopback(uri));

    // localhost is admitted although RFC 8252 §8.3 prefers the literal addresses, because
    // the clients #797 expects (Claude Code, the MCP Inspector, Cursor) register it.
    private static bool IsLoopback(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp && uri.Host is "localhost" or "127.0.0.1" or "[::1]";

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

    private static Result<Uri> InvalidUrl(string description) =>
        Result.Failure<Uri>(new Error(Errors.InvalidRequest, description));

    private static Result<OpenIddictApplicationDescriptor> InvalidMetadata(string description) =>
        Result.Failure<OpenIddictApplicationDescriptor>(new Error("invalid_client_metadata", description));
}
