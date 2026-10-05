namespace Cluckwork.Application.Features.Users.AssignFlock;

[ModuleContract("Access")]
public sealed record AssignFlockCommand(
    Guid UserId, Guid FlockId, string? StepUpToken = null);
