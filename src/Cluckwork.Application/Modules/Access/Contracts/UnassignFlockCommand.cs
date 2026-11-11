namespace Cluckwork.Application.Modules.Access.Contracts;

// The step-up token is a credential, so the record never prints it.
public sealed record UnassignFlockCommand(
    Guid UserId, Guid AssignmentId, string? StepUpToken = null)
{
    public override string ToString() => "[redacted]";
}
