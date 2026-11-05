namespace Cluckwork.Application.Modules.Access.Contracts;

// A token minted before the user's credentials changed: CredentialEpochMiddleware's
// answer, and a grant-spending route's when the change lands while the request
// waits for the user-row lock (#1280, #1277).
public static class CredentialErrorCodes
{
    public const string Superseded = "Auth.CredentialsSuperseded";
}
