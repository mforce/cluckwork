namespace Cluckwork.Application.Features.Users.CreateUser;

// #308 — StepUpToken is the caller's proof-of-recent-auth grant, required
// only when Role is Owner (see CreateUserHandler). It rides a header, never
// the request body proper, but the command carries it alongside the rest of
// the "form data" the handler needs — same shape as ChangeOwnPasswordCommand
// carrying CurrentPassword.
public sealed record CreateUserCommand(
    string Email,
    string Password,
    string Role,
    string? Name = null,
    string? StepUpToken = null);
