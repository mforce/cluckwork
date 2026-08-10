namespace Cluckwork.Application.Features.Users.ChangeUserRole;

// #355 — StepUpToken carries the same shape as CreateUserCommand/
// SetUserPasswordCommand: required only when Role is Owner (see
// ChangeUserRoleHandler). Role is non-nullable — the caller always states an
// explicit target, using the same "Worker" sentinel CreateUserValidator
// defines rather than null (matching CreateUserCommand's own shape).
public sealed record ChangeUserRoleCommand(Guid UserId, string Role, string? StepUpToken = null);
