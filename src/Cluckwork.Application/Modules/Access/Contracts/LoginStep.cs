namespace Cluckwork.Application.Modules.Access.Contracts;

// #1274 "Types": what a correct password earns.
public abstract record LoginStep
{
    private LoginStep() { }

    public sealed record SignedIn(TokenPair Tokens) : LoginStep;

    // The challenge is a credential until it is used: never logged, never in a
    // URL, an event or an audit row (#1274 decision 2).
    // SecondFactors: the proofs the user can answer with, SecondFactorKinds
    // values (#1282 decision 7).
    public sealed record SecondFactorRequired(string Challenge, DateTimeOffset ExpiresAt, IReadOnlyList<string> SecondFactors) : LoginStep;

    public sealed override string ToString() => "[redacted]";
}

// #1285 (record 1282 decision 7): what the pending 401's secondFactors lists.
public static class SecondFactorKinds
{
    public const string Authenticator = "authenticator";
    public const string Passkey = "passkey";
}

// #1285: what the login code step accepts, a code string, parsed after the
// challenge is read, or a passkey assertion made at RequestHost.
[Cluckwork.Application.Common.Credential]
public abstract record SecondFactorInput
{
    private SecondFactorInput() { }

    public sealed record Code(string? Raw) : SecondFactorInput;

    public sealed record Passkey(SecondFactorCode.Passkey Proof, string RequestHost) : SecondFactorInput;

    public sealed override string ToString() => "[redacted]";
}
