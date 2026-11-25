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
        if (request.Credential is not { ValueKind: JsonValueKind.Object } credential)
            return MissingCredential();

        var result = await access.PasskeyLoginAsync(request.Ceremony, credential.GetRawText(), request.FarmCode, http.Host.Host, ct);
        return result.IsSuccess ? SignedIn(response, result.Value, access, env) : LoginPasskeyProblem(result.Error);
    }

    private static IResult PasskeyOptions(Result<PasskeyCeremonyStart> result) =>
        result.IsSuccess
            ? Results.Ok(new PasskeyOptionsResponse(result.Value.Ceremony, JsonSerializer.Deserialize<JsonElement>(result.Value.OptionsJson)))
            : LoginPasskeyProblem(result.Error);

    private static IResult LoginPasskeyProblem(Error error) =>
        Results.Problem(error.Description, title: error.Code, statusCode: error.Code switch
        {
            PasskeyErrorCodes.Off => StatusCodes.Status404NotFound,
            PasskeyErrorCodes.Refused or SecondFactorErrorCodes.FarmSuspended or SecondFactorErrorCodes.ChallengeExpired
                => StatusCodes.Status401Unauthorized,
            _ => throw new InvalidOperationException($"No answer is mapped for {error.Code}."),
        });

    private static IResult MissingCredential() =>
        Results.ValidationProblem(new Dictionary<string, string[]> { ["credential"] = ["The passkey response is missing."] });
}

// The credential is the browser's PublicKeyCredential JSON, passed to Identity
// unparsed. FarmCode is the typed or prefilled farm code, or null.
public sealed record PasskeyLoginRequest(Guid Ceremony, JsonElement? Credential, string? FarmCode = null)
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
