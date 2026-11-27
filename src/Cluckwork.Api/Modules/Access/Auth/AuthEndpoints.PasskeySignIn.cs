using System.Text.Json;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Domain.Common;

namespace Cluckwork.Api.Modules.Access.Auth;

// #1285 (record 1282 "What a caller sees"): passkey sign-in, and the ceremony
// for a passkey at the second sign-in step. A ceremony is bound to the
// request's host, Request.Host.Host, which the routes pass down.
public static partial class AuthEndpoints
{
    private static async Task<IResult> StartPasskeySignIn(HttpRequest http, IAccessModule access, CancellationToken ct) =>
        PasskeyOptions(await access.StartPasskeySignInAsync(http.Host.Host, ct));

    private static async Task<IResult> StartSecondFactorPasskey(
        SecondFactorPasskeyOptionsRequest request, HttpRequest http, IAccessModule access, CancellationToken ct) =>
        PasskeyOptions(await access.StartSecondFactorPasskeyAsync(request.Challenge, http.Host.Host, ct));

    // Decision 12: every refusal is the one 401 Auth.PasskeyRefused body,
    // apart from a suspended farm.
    private static async Task<IResult> PasskeyLogin(
        PasskeyLoginRequest request, HttpRequest http, IAccessModule access, HttpResponse response,
        IWebHostEnvironment env, CancellationToken ct)
    {
        if (CredentialJsonOf(request.Credential) is not { } credential) return MissingCredential();

        var result = await access.PasskeyLoginAsync(request.Ceremony, credential, request.FarmCode, http.Host.Host, ct);
        if (!result.IsSuccess) return LoginStepProblem(result.Error, StatusCodes.Status401Unauthorized);

        var (tokens, farmCode) = result.Value;
        AuthCookies.SetRefreshCookie(response, tokens.AccountId, tokens.RefreshToken, access.RefreshTokenLifetimeDays, CookieSecure(env));
        return Results.Ok(new PasskeySignInResponse(tokens.AccessToken, tokens.AccessTokenExpiry, farmCode));
    }

    private static IResult PasskeyOptions(Result<PasskeyCeremonyStart> result) =>
        result.IsSuccess
            ? Results.Ok(OptionsResponse(result.Value))
            : LoginStepProblem(result.Error, StatusCodes.Status401Unauthorized);

    private static PasskeyOptionsResponse OptionsResponse(PasskeyCeremonyStart start) =>
        new(start.Ceremony, JsonSerializer.Deserialize<JsonElement>(start.OptionsJson));

    // 401 when the user must sign in again, and for a refused passkey sign-in;
    // a refusal at the code step is 400, so apiFetch does not refresh and
    // resend (#1282 decision 12).
    private static IResult LoginStepProblem(Error error, int refusedStatus) =>
        Results.Problem(error.Description, title: error.Code, statusCode: error.Code switch
        {
            PasskeyErrorCodes.Off => StatusCodes.Status404NotFound,
            SecondFactorErrorCodes.ChallengeExpired or SecondFactorErrorCodes.FarmSuspended => StatusCodes.Status401Unauthorized,
            _ => refusedStatus,
        });

    // The browser's PublicKeyCredential JSON, passed to Identity unparsed.
    private static string? CredentialJsonOf(JsonElement? credential) =>
        credential is { ValueKind: JsonValueKind.Object } json ? json.GetRawText() : null;

    private static IResult MissingCredential() =>
        Results.ValidationProblem(new Dictionary<string, string[]> { ["credential"] = ["The passkey response is missing."] });
}

// FarmCode is the typed or prefilled farm code, or null.
public sealed record PasskeyLoginRequest(Guid Ceremony, JsonElement? Credential, string? FarmCode = null)
{
    public override string ToString() => "[redacted]";
}

// As the password login's AccessTokenResponse, plus the signed-in farm's code,
// as stored, which the SPA remembers (record 1282 decision 11). The access
// token is a credential, so the record never prints it.
public sealed record PasskeySignInResponse(string AccessToken, DateTimeOffset AccessTokenExpiry, string FarmCode)
{
    public override string ToString() => "[redacted]";
}

// The challenge is a credential until it is used.
public sealed record SecondFactorPasskeyOptionsRequest(string? Challenge)
{
    public override string ToString() => "[redacted]";
}

// A passkey answer at the code step: the ceremony and the browser's JSON.
public sealed record PasskeyAnswer(Guid Ceremony, JsonElement? Credential)
{
    public override string ToString() => "[redacted]";
}
