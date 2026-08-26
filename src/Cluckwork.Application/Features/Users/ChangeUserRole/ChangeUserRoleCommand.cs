namespace Cluckwork.Application.Features.Users.ChangeUserRole;

// #355/#360 — StepUpToken carries the same proof as CreateUserCommand and
// SetUserPasswordCommand and is required for every requested role.
// Role is non-nullable — the caller always states an
// explicit target, using the same "Worker" sentinel CreateUserValidator
// defines rather than null (matching CreateUserCommand's own shape).
public sealed record ChangeUserRoleCommand(Guid UserId, string Role, string? StepUpToken = null);
