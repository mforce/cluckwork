namespace Cluckwork.Application.Modules.Access.Contracts;

// #1274 "Types": what a correct password earns.
public abstract record LoginStep
{
    private LoginStep() { }

    public sealed record SignedIn(TokenPair Tokens) : LoginStep;

    // The challenge is a credential until it is used: never logged, never in a
    // URL, an event or an audit row (#1274 decision 2).
    public sealed record SecondFactorRequired(string Challenge, DateTimeOffset ExpiresAt) : LoginStep;

    public sealed override string ToString() => "[redacted]";
}
