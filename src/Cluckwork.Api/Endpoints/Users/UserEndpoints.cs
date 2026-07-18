namespace Cluckwork.Api.Endpoints.Users;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Users.CreateUser;
using Cluckwork.Infrastructure.Persistence;
using FluentValidation;

// #73 — minimal user management: enough for an admin to create a worker (or
// another admin) and see who exists. Full user administration is the RBAC slice.
public static class UserEndpoints
{
    public static RouteGroupBuilder MapUserEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/", CreateUser)
            .WithName("CreateUser")
            .WithSummary("Create a user for this account (Admin or Worker).");

        group.MapGet("/", ListUsers)
            .WithName("ListUsers")
            .WithSummary("List this account's users and their role.");

        return group;
    }

    private static async Task<IResult> CreateUser(
        CreateUserRequest request,
        CreateUserHandler handler,
        IValidator<CreateUserCommand> validator,
        TenantContext tenant,
        CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        var command = new CreateUserCommand(request.Email, request.Password, request.Role);
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            return Results.ValidationProblem(validation.ToDictionary());

        var result = await handler.HandleAsync(command, tenant.AccountId, ct);
        return result.IsSuccess
            ? Results.Created("/api/v1/users", new { Id = result.Value })
            : Results.Problem(result.Error.Description, statusCode: 422, title: result.Error.Code);
    }

    private static async Task<IResult> ListUsers(
        IIdentityProvider identity, TenantContext tenant, CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();
        var users = await identity.ListUsersAsync(tenant.AccountId, ct);
        return Results.Ok(users.Select(u => new UserResponse(u.Id, u.Email, u.DisplayName, u.Role)));
    }
}

public sealed record CreateUserRequest(string Email, string Password, string Role);

public sealed record UserResponse(Guid Id, string Email, string? DisplayName, string Role);
