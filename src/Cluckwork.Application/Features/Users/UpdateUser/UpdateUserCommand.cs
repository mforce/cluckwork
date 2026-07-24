namespace Cluckwork.Application.Features.Users.UpdateUser;

// #163 — edit an existing user's display name. Name is nullable: passing null (or
// blank) clears it back to "—". Role/password editing are separate slices.
public sealed record UpdateUserCommand(Guid UserId, string? Name);
