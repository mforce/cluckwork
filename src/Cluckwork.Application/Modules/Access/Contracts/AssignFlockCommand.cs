namespace Cluckwork.Application.Modules.Access.Contracts;

public sealed record AssignFlockCommand(
    Guid UserId, Guid FlockId, string? StepUpToken = null);
