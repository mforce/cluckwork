namespace Cluckwork.Application.Features.Users.ChangeUserEmail;

public sealed record ChangeUserEmailCommand(Guid UserId, string Email, string? StepUpToken);
