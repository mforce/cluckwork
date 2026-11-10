namespace Cluckwork.Application.Modules.Access.Contracts;

// #1277: what the /api/v1/auth/two-factor routes hand back (#1274 "Types").
// RequiredSince is the Owner grace start (#1281), null until the first request sets it.
public readonly record struct TwoFactorStatus(bool Enabled, int RecoveryCodesLeft, DateTimeOffset? RequiredSince);

public sealed record EnrolmentStart(string Key, string OtpauthUri)
{
    public override string ToString() => "[redacted]";
}

// RecoveryCodes is set for a first enrolment only; a replacement keeps the codes.
public sealed record EnrolmentConfirmation(TokenPair Tokens, IReadOnlyList<string>? RecoveryCodes)
{
    public override string ToString() => "[redacted]";
}

public static class EnrolmentErrorCodes
{
    public const string CodeIncorrect = "Auth.EnrolmentCodeIncorrect";
    public const string Expired = "Auth.EnrolmentExpired";
    public const string NotEnabled = "Auth.TwoFactorNotEnabled";
    // #1281 — an unenrolled Owner past the grace deadline; 403, never 401.
    public const string EnrolmentRequired = "Auth.TwoFactorEnrolmentRequired";
    // #1281 — an Owner replaces a lost authenticator instead.
    public const string OwnerCannotTurnOff = "Auth.OwnerCannotTurnOffTwoFactor";
}

// AttemptsLeft is null for a malformed code, which counts nothing.
public sealed record EnrolmentCodeIncorrectError(int? AttemptsLeft)
    : Error(EnrolmentErrorCodes.CodeIncorrect, "That code did not work. Check the code in your authenticator app.");
