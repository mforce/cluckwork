namespace Cluckwork.Application.Modules.Access.Contracts;

[ModuleContract("Access")]
public sealed record UnassignFlockCommand(
    Guid UserId, Guid AssignmentId, string? StepUpToken = null);
