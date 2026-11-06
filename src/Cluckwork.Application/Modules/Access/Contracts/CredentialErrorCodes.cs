namespace Cluckwork.Application.Modules.Access.Contracts;

// A token minted before the user's credentials changed: CredentialEpochMiddleware's
// answer, and a grant-spending route's when the change lands while the request
// waits for the user-row lock (#1280, #1277).
public static class CredentialErrorCodes
{
    public const string Superseded = "Auth.CredentialsSuperseded";

    // A refresh token logout retired (#1277). Internal to refresh: the route
    // answers it as Identity.InvalidRefreshToken, and uses it only to clear
    // the legacy cookie that carried it.
    public const string RetiredByLogout = "Identity.RefreshTokenRetiredByLogout";
}
