namespace Cluckwork.Application.Features.Users.ChangeUserEmail;

[ModuleContract("Access")]
public sealed record ChangeUserEmailCommand(Guid UserId, string Email, string? StepUpToken);
