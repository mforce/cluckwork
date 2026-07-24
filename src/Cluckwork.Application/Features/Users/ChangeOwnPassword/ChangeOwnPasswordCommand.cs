namespace Cluckwork.Application.Features.Users.ChangeOwnPassword;

// #165 — self-service: any signed-in user changes their OWN password by proving
// the current one. On success every session is revoked and the caller is handed
// a fresh pair, so other devices are signed out but this one stays in.
public sealed record ChangeOwnPasswordCommand(string CurrentPassword, string NewPassword);
