namespace Cluckwork.Application.Features.Users.AssignFlock;

[ModuleContract("Access")]
public sealed record UnassignFlockCommand(
    Guid UserId, Guid AssignmentId, string? StepUpToken = null);
