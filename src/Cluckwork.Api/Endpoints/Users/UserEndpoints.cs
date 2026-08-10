namespace Cluckwork.Api.Endpoints.Users;

using Cluckwork.Api.Hosting;
using Cluckwork.Api.Validation;
using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Users;
using Cluckwork.Application.Features.Users.AssignFlock;
using Cluckwork.Application.Features.Users.ChangeUserRole;
using Cluckwork.Application.Features.Users.CreateUser;
using Cluckwork.Application.Features.Users.SetUserPassword;
using Cluckwork.Application.Features.Users.UpdateUser;
using Cluckwork.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;

// #73 — minimal user management: enough for an admin to create a worker (or
// another admin) and see who exists. Full user administration is the RBAC slice.
public static class UserEndpoints
{
    public static RouteGroupBuilder MapUserEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/", CreateUser)
            // #309 — 8 KB, not 4 KB: email + role + name + a ≤256-char password,
            // with System.Text.Json's default \uXXXX escaping of non-ASCII
            // (measured ~3.9 KB worst case against the old 4 KB cap — only 5%
            // margin, fragile to any future field addition). 8 KB caps the body
            // ahead of binding and the new-password PBKDF2 hash with real margin.
            .WithMaxRequestBodyBytes(8192)
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
            // #309 — a single ≤256-char password; 2 KB caps the body ahead of
            // binding and the new-password PBKDF2 hash.
            .WithMaxRequestBodyBytes(2048)
            .WithName("SetUserPassword")
            .WithSummary("Set a user's password and revoke their sessions.");

        // #103 — flock scoping for workers (spec §5.3). Owner-only like the
        // rest of the group.
        // #355 — promote/demote an existing user. Owner-only like the whole
        // group. Self-role-change is blocked before validation (see below);
        // granting Owner requires step-up (#308), same gating as CreateUser.
        group.MapPut("/{id:guid}/role", ChangeUserRole)
            // A single short role name (max "ReadOnly" = 8 chars) plus JSON
            // envelope overhead — two orders of magnitude smaller than
            // SetUserPassword's 2048-byte password field, so 512 bytes gives
            // comfortable margin ahead of binding.
            .WithMaxRequestBodyBytes(512)
            .WithName("ChangeUserRole")
            .WithSummary("Change a user's role (promote/demote) and revoke their sessions.");

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
        [FromHeader(Name = Cluckwork.Api.Endpoints.Auth.AuthEndpoints.StepUpHeaderName)] string? stepUpToken,
        CreateUserHandler handler,
        IValidator<CreateUserCommand> validator,
        TenantContext tenant,
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        if (!tenant.IsResolved || !currentUser.IsResolved) return Results.Unauthorized();

        var command = new CreateUserCommand(
            request.Email, request.Password, request.Role, request.Name, stepUpToken);
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            return ValidationResponse.Problem(validation);

        var result = await handler.HandleAsync(command, tenant.AccountId, currentUser.UserId, ct);
        if (result.IsSuccess)
            return Results.Created("/api/v1/users", new { Id = result.Value });
        // #308 — a missing/invalid step-up grant is a 403 (authenticated, but
        // lacking a required additional proof), distinct from the 422s below
        // used for ordinary validation/domain failures.
        return result.Error.Code == Cluckwork.Application.Common.StepUpErrorCodes.Required
            ? Results.Problem(result.Error.Description, statusCode: StatusCodes.Status403Forbidden, title: result.Error.Code)
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
        [FromHeader(Name = Cluckwork.Api.Endpoints.Auth.AuthEndpoints.StepUpHeaderName)] string? stepUpToken,
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

        var command = new SetUserPasswordCommand(id, request.NewPassword, stepUpToken);
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            return ValidationResponse.Problem(validation);

        var result = await handler.HandleAsync(command, tenant.AccountId, currentUser.UserId, ct);
        if (result.IsSuccess) return Results.NoContent();
        // #308 — a missing/invalid step-up grant is a 403, checked before the
        // generic NotFound/422 mapping below.
        if (result.Error.Code == Cluckwork.Application.Common.StepUpErrorCodes.Required)
            return Results.Problem(result.Error.Description, statusCode: StatusCodes.Status403Forbidden, title: result.Error.Code);
        return result.Error.Code.EndsWith(".NotFound", StringComparison.Ordinal)
            ? Results.NotFound()
            : Results.Problem(result.Error.Description, statusCode: 422, title: result.Error.Code);
    }

    private static async Task<IResult> ChangeUserRole(
        Guid id,
        ChangeUserRoleRequest request,
        [FromHeader(Name = Cluckwork.Api.Endpoints.Auth.AuthEndpoints.StepUpHeaderName)] string? stepUpToken,
        ChangeUserRoleHandler handler,
        IValidator<ChangeUserRoleCommand> validator,
        TenantContext tenant,
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        if (!tenant.IsResolved || !currentUser.IsResolved) return Results.Unauthorized();

        // #355 — an Owner may not change their OWN role here. Same shape as
        // SetUserPassword's CannotSetOwnPassword: self-targeting through this
        // path could strip the caller's own Owner-ness (or, for a non-Owner
        // target, revoke the caller's own live session) with no re-proof.
        if (currentUser.UserId == id)
            return Results.Problem(
                "You cannot change your own role. Ask another Owner.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Users.CannotChangeOwnRole");

        var command = new ChangeUserRoleCommand(id, request.Role, stepUpToken);
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            return ValidationResponse.Problem(validation);

        var result = await handler.HandleAsync(command, tenant.AccountId, currentUser.UserId, ct);
        if (result.IsSuccess) return Results.NoContent();
        // #308 — a missing/invalid step-up grant, or a stale actor no longer
        // Owner, is a 403 (authenticated, but lacking a required proof of
        // current authorization) — distinct from the 404/409/422s below.
        if (result.Error.Code is Cluckwork.Application.Common.StepUpErrorCodes.Required or "Auth.Forbidden")
            return Results.Problem(result.Error.Description, statusCode: StatusCodes.Status403Forbidden, title: result.Error.Code);
        if (result.Error.Code.EndsWith(".NotFound", StringComparison.Ordinal))
            return Results.NotFound();
        return result.Error.Code.EndsWith(".Conflict", StringComparison.Ordinal)
            ? Results.Problem(result.Error.Description, statusCode: StatusCodes.Status409Conflict, title: result.Error.Code)
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

public sealed record ChangeUserRoleRequest(string Role);

public sealed record UserResponse(Guid Id, string Email, string? DisplayName, string Role);

public sealed record AssignFlockRequest(Guid FlockId);

public sealed record FlockAssignmentResponse(Guid Id, Guid? FlockId);
