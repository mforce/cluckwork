using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace Cluckwork.Api.Modules.Access.Auth;

// #1284 (record 1282 "What a caller sees"): registering, listing, renaming and
// removing the caller's own passkeys. Under /api/v1/auth/passkeys, on
// ResponseNotCacheable, so no route needs an Idempotency-Key, a refused
// finish's ceremony delete commits, and no pair reaches idempotency_records.
public static partial class AuthEndpoints
{
    // A name the user reads in Account → Security.
    internal const int PasskeyNameMaxLength = 64;

    private static async Task<IResult> StartPasskeyRegistration(
        IAccessModule access, ICurrentUser currentUser, TenantContext tenant, CancellationToken ct)
    {
        if (!currentUser.IsResolved || !tenant.IsResolved) return Results.Unauthorized();

        var result = await access.StartPasskeyRegistrationAsync(tenant.AccountId, currentUser.UserId, ct);
        return result.IsSuccess
            ? Results.Ok(new PasskeyOptionsResponse(result.Value.Ceremony, JsonSerializer.Deserialize<JsonElement>(result.Value.OptionsJson)))
            : StepUpRouteProblem(result.Error);
    }

    // 201 with the passkey, or 200 with the fresh pair too when it was the
    // user's first factor and every other session ended (decision 16).
    private static async Task<IResult> RegisterPasskey(
        RegisterPasskeyRequest request,
        [FromHeader(Name = StepUpHeaderName)] string? stepUpToken,
        IAccessModule access, ICurrentUser currentUser, TenantContext tenant, ClaimsPrincipal user,
        HttpResponse response, IWebHostEnvironment env, CancellationToken ct)
    {
        if (!currentUser.IsResolved || !tenant.IsResolved || ActingEpoch(user) is not { } epoch)
            return Results.Unauthorized();
        if (PasskeyName(request.Name, out var name) is { } invalid) return invalid;
        if (request.Credential is not { ValueKind: JsonValueKind.Object } credential)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["credential"] = ["The passkey response is missing."] });

        var result = await access.RegisterPasskeyAsync(tenant.AccountId, currentUser.UserId, epoch, stepUpToken,
            request.Ceremony, credential.GetRawText(), name, ct);
        if (!result.IsSuccess) return StepUpRouteProblem(result.Error);

        if (result.Value.Tokens is not { } pair)
            return Results.Created((string?)null, new PasskeyRegisteredResponse(result.Value.Passkey, null, null));
        AuthCookies.SetRefreshCookie(response, pair.AccountId, pair.RefreshToken, access.RefreshTokenLifetimeDays, CookieSecure(env));
        return Results.Ok(new PasskeyRegisteredResponse(result.Value.Passkey, pair.AccessToken, pair.AccessTokenExpiry));
    }

    private static async Task<IResult> ListPasskeys(
        IAccessModule access, ICurrentUser currentUser, TenantContext tenant, CancellationToken ct)
    {
        if (!currentUser.IsResolved || !tenant.IsResolved) return Results.Unauthorized();
        return Results.Ok(await access.ListPasskeysAsync(tenant.AccountId, currentUser.UserId, ct));
    }

    private static async Task<IResult> RenamePasskey(
        string id, RenamePasskeyRequest request,
        IAccessModule access, ICurrentUser currentUser, TenantContext tenant, CancellationToken ct)
    {
        if (!currentUser.IsResolved || !tenant.IsResolved) return Results.Unauthorized();
        if (PasskeyName(request.Name, out var name) is { } invalid) return invalid;
        if (CredentialId(id) is not { } credentialId) return Results.NotFound();

        var result = await access.RenamePasskeyAsync(tenant.AccountId, currentUser.UserId, credentialId, name, ct);
        return result.IsSuccess ? Results.NoContent() : StepUpRouteProblem(result.Error);
    }

    // Decision 10: every other session and connected app ends; this one gets
    // a fresh pair. Works while passkeys are off.
    private static async Task<IResult> RemovePasskey(
        string id,
        [FromHeader(Name = StepUpHeaderName)] string? stepUpToken,
        IAccessModule access, ICurrentUser currentUser, TenantContext tenant, ClaimsPrincipal user,
        HttpResponse response, IWebHostEnvironment env, CancellationToken ct)
    {
        if (!currentUser.IsResolved || !tenant.IsResolved || ActingEpoch(user) is not { } epoch)
            return Results.Unauthorized();
        if (CredentialId(id) is not { } credentialId) return Results.NotFound();

        var result = await access.RemovePasskeyAsync(tenant.AccountId, currentUser.UserId, epoch, stepUpToken, credentialId, ct);
        if (!result.IsSuccess) return StepUpRouteProblem(result.Error);

        var pair = result.Value;
        AuthCookies.SetRefreshCookie(response, pair.AccountId, pair.RefreshToken, access.RefreshTokenLifetimeDays, CookieSecure(env));
        return Results.Ok(new AccessTokenResponse(pair.AccessToken, pair.AccessTokenExpiry));
    }

    // The route's {id} is the credential id, base64url; anything else names no passkey.
    private static byte[]? CredentialId(string id)
    {
        try
        {
            return WebEncoders.Base64UrlDecode(id);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    // The trimmed name, or the 400 for one that is empty, too long or holds
    // a control character (r1 item 8), before anything is consumed or saved.
    private static IResult? PasskeyName(string? raw, out string name)
    {
        name = raw?.Trim() ?? string.Empty;
        return name.Length is 0 or > PasskeyNameMaxLength || name.Any(char.IsControl)
            ? Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["name"] = [$"Give the passkey a name of 1 to {PasskeyNameMaxLength} characters, with no control characters."],
            })
            : null;
    }
}

public sealed record PasskeyOptionsResponse(Guid Ceremony, JsonElement Options);

// The credential is the browser's PublicKeyCredential JSON, passed to Identity unparsed.
public sealed record RegisterPasskeyRequest(Guid Ceremony, JsonElement? Credential, string? Name)
{
    public override string ToString() => "[redacted]";
}

// Every passkey request body redacts (record 1282 G7.7).
public sealed record RenamePasskeyRequest(string? Name)
{
    public override string ToString() => "[redacted]";
}

public sealed record PasskeyRegisteredResponse(
    PasskeySummary Passkey,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AccessToken,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? AccessTokenExpiry)
{
    public override string ToString() => "[redacted]";
}
