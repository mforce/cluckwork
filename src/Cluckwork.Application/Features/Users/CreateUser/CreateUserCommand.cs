namespace Cluckwork.Application.Features.Users.CreateUser;

public sealed record CreateUserCommand(
    string Email,
    string Password,
    string Role,
    string? Name = null);
