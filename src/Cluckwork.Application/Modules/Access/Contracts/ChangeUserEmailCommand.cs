namespace Cluckwork.Application.Modules.Access.Contracts;

public sealed record ChangeUserEmailCommand(Guid UserId, string Email, string? StepUpToken);
