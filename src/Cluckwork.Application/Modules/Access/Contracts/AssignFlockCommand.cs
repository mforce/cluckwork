namespace Cluckwork.Application.Modules.Access.Contracts;

// The step-up token is a credential, so the record never prints it.
public sealed record AssignFlockCommand(
    Guid UserId, Guid FlockId, string? StepUpToken = null)
{
    public override string ToString() => "[redacted]";
}
