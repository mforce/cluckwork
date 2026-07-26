namespace Cluckwork.Api.Endpoints.Users;

using Cluckwork.Api.Validation;
using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Users;
using Cluckwork.Application.Features.Users.AssignFlock;
using Cluckwork.Application.Features.Users.CreateUser;
using Cluckwork.Application.Features.Users.SetUserPassword;
using Cluckwork.Application.Features.Users.UpdateUser;
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

        // #163 — edit a user's display name (Owner-only, like the whole group).
        group.MapPut("/{id:guid}", UpdateUser)
            .WithName("UpdateUser")
            .WithSummary("Update a user's display name.");

        // #165 — set a user's password without the current one (the forgot-password
        // path; there is no email reset). Owner-only, and it signs the target out
        // of every device.
        group.MapPut("/{id:guid}/password", SetUserPassword)
            .WithName("SetUserPassword")
            .WithSummary("Set a user's password and revoke their sessions.");

        // #103 — flock scoping for workers (spec §5.3). Owner-only like the
        // rest of the group.
        group.MapGet("/{id:guid}/flock-assignments", ListAssignments)
            .WithName("ListFlockAssignments")
            .WithSummary("A user's flock assignments. No rows = account-wide access.");

        group.MapPost("/{id:guid}/flock-assignments", AssignFlock)
            .WithName("AssignFlock")
            .WithSummary("Assign a flock to a worker. The first assignment narrows them to assigned flocks only.");

        group.MapDelete("/{id:guid}/flock-assignments/{assignmentId:guid}", UnassignFlock)
            .WithName("UnassignFlock")
            .WithSummary("Remove a flock assignment. Removing the last one restores account-wide access.");

        return group;
    }

    private static async Task<IResult> ListAssignments(
        Guid id, IUserRoleAssignmentRepository assignments, TenantContext tenant, CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();
        var list = await assignments.ListByUserAsync(id, ct);
        return Results.Ok(list.Select(a => new FlockAssignmentResponse(a.Id, a.FlockId)));
    }

    private static async Task<IResult> AssignFlock(
        Guid id, AssignFlockRequest request, AssignFlockHandler handler,
        TenantContext tenant, CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();
        if (request.FlockId == Guid.Empty)
            return ValidationResponse.Problem(new Dictionary<string, string[]>
            {
                ["flockId"] = ["A flock id is required."],
            });
        var result = await handler.HandleAsync(id, request.FlockId, tenant.AccountId, ct);
        if (result.IsSuccess)
            return Results.Created($"/api/v1/users/{id}/flock-assignments", new { Id = result.Value });
        if (result.Error.Code.EndsWith(".NotFound", StringComparison.Ordinal))
            return Results.NotFound();
        return result.Error.Code == "Users.AlreadyAssigned"
            ? Results.Problem(result.Error.Description, statusCode: StatusCodes.Status409Conflict, title: result.Error.Code)
            : Results.Problem(result.Error.Description, statusCode: 422, title: result.Error.Code);
    }

    private static async Task<IResult> UnassignFlock(
        Guid id, Guid assignmentId, UnassignFlockHandler handler,
        TenantContext tenant, CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();
        var result = await handler.HandleAsync(id, assignmentId, ct);
        return result.IsSuccess
            ? Results.NoContent()
            : Results.NotFound();
    }

    private static async Task<IResult> CreateUser(
        CreateUserRequest request,
        CreateUserHandler handler,
        IValidator<CreateUserCommand> validator,
        TenantContext tenant,
        CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        var command = new CreateUserCommand(request.Email, request.Password, request.Role, request.Name);
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            return ValidationResponse.Problem(validation);

        var result = await handler.HandleAsync(command, tenant.AccountId, ct);
        return result.IsSuccess
            ? Results.Created("/api/v1/users", new { Id = result.Value })
            : Results.Problem(result.Error.Description, statusCode: 422, title: result.Error.Code);
    }

    private static async Task<IResult> UpdateUser(
        Guid id,
        UpdateUserRequest request,
        UpdateUserHandler handler,
        IValidator<UpdateUserCommand> validator,
        TenantContext tenant,
        CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        var command = new UpdateUserCommand(id, request.Name);
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            return ValidationResponse.Problem(validation);

        var result = await handler.HandleAsync(command, tenant.AccountId, ct);
        if (result.IsSuccess) return Results.NoContent();
        if (result.Error.Code.EndsWith(".NotFound", StringComparison.Ordinal))
            return Results.NotFound();
        return result.Error.Code.EndsWith(".Conflict", StringComparison.Ordinal)
            ? Results.Problem(result.Error.Description, statusCode: StatusCodes.Status409Conflict, title: result.Error.Code)
            : Results.Problem(result.Error.Description, statusCode: 422, title: result.Error.Code);
    }

    private static async Task<IResult> SetUserPassword(
        Guid id,
        SetUserPasswordRequest request,
        SetUserPasswordHandler handler,
        IValidator<SetUserPasswordCommand> validator,
        TenantContext tenant,
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        // #165 review — an Owner may not reset their OWN password here. This path
        // deliberately skips the current-password proof, so allowing self-targeting
        // would turn a stolen access token (good for ~15 min) into a permanent
        // credential takeover, bypassing the very check /auth/change-password
        // exists to enforce. It would also revoke the caller's own sessions with
        // no re-issue. Self-changes go through the Account screen.
        if (currentUser.IsResolved && currentUser.UserId == id)
            return Results.Problem(
                "Use the Account screen to change your own password.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Users.CannotSetOwnPassword");

        var command = new SetUserPasswordCommand(id, request.NewPassword);
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            return ValidationResponse.Problem(validation);

        var result = await handler.HandleAsync(command, tenant.AccountId, ct);
        if (result.IsSuccess) return Results.NoContent();
        return result.Error.Code.EndsWith(".NotFound", StringComparison.Ordinal)
            ? Results.NotFound()
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

public sealed record CreateUserRequest(string Email, string Password, string Role, string? Name = null);

public sealed record UpdateUserRequest(string? Name);

public sealed record SetUserPasswordRequest(string NewPassword);

public sealed record UserResponse(Guid Id, string Email, string? DisplayName, string Role);

public sealed record AssignFlockRequest(Guid FlockId);

public sealed record FlockAssignmentResponse(Guid Id, Guid? FlockId);
