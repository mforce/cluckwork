namespace Cluckwork.Application.Features.Users.SetUserPassword;

// #165 — an Owner sets another user's password WITHOUT knowing the current one
// (the forgot-password path; there is no email reset). Account-scoped in the
// provider, and it revokes the target's sessions.
//
// #308 — StepUpToken is the caller's proof-of-recent-auth grant, required
// only when the TARGET currently holds the Owner role (see
// SetUserPasswordHandler).
public sealed record SetUserPasswordCommand(Guid UserId, string NewPassword, string? StepUpToken = null);
