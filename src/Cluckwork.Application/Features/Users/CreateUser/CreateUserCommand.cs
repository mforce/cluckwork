namespace Cluckwork.Application.Features.Users.CreateUser;

// #308/#360 — StepUpToken is the caller's proof-of-recent-auth grant, required
// for every interactive user creation regardless of Role. It rides a header,
// never the request body proper, but the command carries it alongside the rest
// of the form data the handler needs.
public sealed record CreateUserCommand(
    string Email,
    string Password,
    string Role,
    string? Name = null,
    string? StepUpToken = null);
