namespace Cluckwork.Application.Modules.Access.Contracts;

// The step-up token is a credential, so the record never prints it.
public sealed record StepUpGrant(string Token, DateTimeOffset ExpiresAt)
{
    public override string ToString() => "[redacted]";
}
