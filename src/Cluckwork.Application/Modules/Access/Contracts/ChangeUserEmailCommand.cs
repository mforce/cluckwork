namespace Cluckwork.Application.Modules.Access.Contracts;

// The step-up token is a credential, so the record never prints it.
public sealed record ChangeUserEmailCommand(Guid UserId, string Email, string? StepUpToken)
{
    public override string ToString() => "[redacted]";
}
