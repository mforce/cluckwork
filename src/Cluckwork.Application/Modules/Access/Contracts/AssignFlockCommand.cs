namespace Cluckwork.Application.Modules.Access.Contracts;

[ModuleContract("Access")]
public sealed record AssignFlockCommand(
    Guid UserId, Guid FlockId, string? StepUpToken = null);
