namespace Cluckwork.Application.Modules.Access.Contracts;

// #1274 decision 6: the user's daily budget of wrong authenticator codes is
// spent. Endpoints choose the 429 and its Retry-After by this type.
public sealed record RetryLaterError(TimeSpan RetryAfter)
    : Error("Auth.SecondFactorThrottled",
        "Too many wrong authenticator codes for this account. Use a recovery code or try again later.");
