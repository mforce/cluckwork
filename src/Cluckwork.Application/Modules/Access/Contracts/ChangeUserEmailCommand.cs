namespace Cluckwork.Application.Modules.Access.Contracts;

[ModuleContract("Access")]
public sealed record ChangeUserEmailCommand(Guid UserId, string Email, string? StepUpToken);
