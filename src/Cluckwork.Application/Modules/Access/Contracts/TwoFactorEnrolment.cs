namespace Cluckwork.Application.Modules.Access.Contracts;

// #1277: what the /api/v1/auth/two-factor routes hand back (#1274 "Types").
// RequiredSince joins the status in #1281.
public readonly record struct TwoFactorStatus(bool Enabled, int RecoveryCodesLeft);

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
}

// AttemptsLeft is null for a malformed code, which counts nothing.
public sealed record EnrolmentCodeIncorrectError(int? AttemptsLeft)
    : Error(EnrolmentErrorCodes.CodeIncorrect, "That code did not work. Check the code in your authenticator app.");
