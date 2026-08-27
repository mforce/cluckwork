namespace Cluckwork.Application.Features.Users.AssignFlock;

public sealed record AssignFlockCommand(
    Guid UserId, Guid FlockId, string? StepUpToken = null);
