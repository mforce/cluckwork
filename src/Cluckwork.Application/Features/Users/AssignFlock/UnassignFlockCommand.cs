namespace Cluckwork.Application.Features.Users.AssignFlock;

public sealed record UnassignFlockCommand(
    Guid UserId, Guid AssignmentId, string? StepUpToken = null);
