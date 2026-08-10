namespace Cluckwork.Application.Features.Users.DisableUser;

// #356 — Reason is OPTIONAL free text recorded on the audit row (a mandatory
// one gets typed "x"). StepUpToken is REQUIRED, unlike ChangeUserRoleCommand's
// conditional shape: disabling any user removes their access outright, so
// there is no "ordinary farm administration" case here to leave ungated.
public sealed record DisableUserCommand(Guid UserId, string? Reason = null, string? StepUpToken = null);
