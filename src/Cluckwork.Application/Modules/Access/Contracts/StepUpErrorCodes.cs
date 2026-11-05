namespace Cluckwork.Application.Modules.Access.Contracts;

public static class StepUpErrorCodes
{
    // #308 — every step-up rejection reason (missing, malformed, expired,
    // replayed, wrong-account/user, stamp-revoked, logout-revoked) maps to
    // this one code so a gated endpoint's caller cannot enumerate WHY a grant
    // failed.
    public const string Required = "Identity.StepUpRequired";

    // #1274 decision 11: an enrolled user's step-up could not be saved, so no
    // grant was issued. The user steps up again with the next code.
    public const string Retry = "Auth.StepUpRetry";
}

// A token minted before the user's credentials changed: CredentialEpochMiddleware's
// answer, and a grant-spending route's when the change lands while the request
// waits for the user-row lock (#1280, #1277).
public static class CredentialErrorCodes
{
    public const string Superseded = "Auth.CredentialsSuperseded";
}
