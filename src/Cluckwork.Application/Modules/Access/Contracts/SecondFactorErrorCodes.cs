namespace Cluckwork.Application.Modules.Access.Contracts;

// #1274 "What a caller sees": the code step's answers. The two that send the
// user back to the password step are 401; every other refusal is 400 or 429.
public static class SecondFactorErrorCodes
{
    public const string Required = "Auth.SecondFactorRequired";
    public const string ChallengeExpired = "Auth.SecondFactorChallengeExpired";
    public const string FarmSuspended = "Auth.FarmSuspended";
    public const string Incorrect = "Auth.SecondFactorIncorrect";
    public const string KeyUnavailable = "Auth.SecondFactorKeyUnavailable";
}
