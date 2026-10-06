namespace Cluckwork.Application.Modules.Access.Contracts;

public sealed record UnassignFlockCommand(
    Guid UserId, Guid AssignmentId, string? StepUpToken = null);
