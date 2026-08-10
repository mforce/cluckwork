namespace Cluckwork.Application.Features.Users.EnableUser;

// #356 — no reason field: an enable restores the state the account was in
// before, and the audit row already records who and when. StepUpToken is
// required for the same reason as on the disable side — re-enabling an Owner
// restores exactly the access a disable took away.
public sealed record EnableUserCommand(Guid UserId, string? StepUpToken = null);
