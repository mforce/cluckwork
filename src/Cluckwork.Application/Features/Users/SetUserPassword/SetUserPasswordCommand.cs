namespace Cluckwork.Application.Features.Users.SetUserPassword;

// #165 — an Owner sets another user's password WITHOUT knowing the current one
// (the forgot-password path; there is no email reset). Account-scoped in the
// provider, and it revokes the target's sessions.
//
// #308/#360 — StepUpToken is the caller's proof-of-recent-auth grant, required
// for every administrative reset regardless of the target's current role.
public sealed record SetUserPasswordCommand(Guid UserId, string NewPassword, string? StepUpToken = null);
