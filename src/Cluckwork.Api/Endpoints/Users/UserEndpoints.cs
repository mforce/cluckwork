namespace Cluckwork.Api.Endpoints.Users;

using Cluckwork.Api.Hosting;
using Cluckwork.Api.Validation;
using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Users;
using Cluckwork.Application.Features.Users.AssignFlock;
using Cluckwork.Application.Features.Users.ChangeUserRole;
using Cluckwork.Application.Features.Users.ChangeUserEmail;
using Cluckwork.Application.Features.Users.CreateUser;
using Cluckwork.Application.Features.Users.DisableUser;
using Cluckwork.Application.Features.Users.EnableUser;
using Cluckwork.Application.Features.Users.SetUserPassword;
using Cluckwork.Application.Features.Users.UpdateUser;
using Cluckwork.Domain.Common;
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

        group.MapPut("/{id:guid}/email", ChangeUserEmail)
            .WithMaxRequestBodyBytes(2048)
            .WithName("ChangeUserEmail")
            .WithSummary("Change a user's login email and revoke their sessions.");

        // #356 — disable / re-enable. Separate verbs (following
        // POST /products/{id}/deactivate) rather than a PUT carrying a boolean:
        // the two directions are not symmetric on the CREDENTIAL EPOCH — a
        // disable bumps it and revokes every refresh token, while an enable
        // deliberately leaves the epoch alone and does not revive sessions
        // (that asymmetry is what keeps every pre-disable access token dead).
        // Both DO rotate the security stamp (round-3/5/7 review of #492): an
        // enable's rotation is required, not optional — it stops a stale
        // concurrent password-reset write from silently restoring DisabledAt.
        // Owner-only via the group; step-up is required in BOTH directions,
        // because re-enabling an Owner restores exactly the access disabling
        // one took away.
        group.MapPost("/{id:guid}/disable", DisableUser)
            // A single optional ≤200-char reason. System.Text.Json escapes
            // non-ASCII as \uXXXX (6 bytes/char), so the worst case is ~1.2 KB
            // — 512 bytes would reject a legitimate non-Latin reason at the
            // transport layer with a 413 nobody could act on. 2 KB matches
            // SetUserPassword's cap and leaves real margin (#309's measurement).
            .WithMaxRequestBodyBytes(2048)
            .WithName("DisableUser")
            .WithSummary("Disable a user: revoke every session and refuse further sign-in.");

        group.MapPost("/{id:guid}/enable", EnableUser)
            // Binds no body — but IdempotencyMiddleware buffers the request body
            // unconditionally to hash it, and with no per-endpoint cap that
            // falls back to Kestrel's 30 MB default. 512 bytes is ample for a
            // request whose only content is the route id and two headers.
            .WithMaxRequestBodyBytes(512)
            .WithName("EnableUser")
            .WithSummary("Re-enable a disabled user. Does NOT restore their previous sessions.");

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
        // #308/#360 — every interactive creation requires a step-up grant
        // regardless of the created user's role. A missing/invalid grant is a
        // 403 (authenticated, but lacking the required additional proof),
        // distinct from the 422s below used for ordinary validation/domain
        // failures.
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
        // #308/#360 — every administrative reset requires a step-up grant
        // regardless of the target's role. A missing/invalid grant is a 403,
        // checked before the generic NotFound/422 mapping below, so a
        // proof-less caller cannot distinguish user ids.
        if (result.Error.Code == Cluckwork.Application.Common.StepUpErrorCodes.Required)
            return Results.Problem(result.Error.Description, statusCode: StatusCodes.Status403Forbidden, title: result.Error.Code);
        if (result.Error.Code.EndsWith(".NotFound", StringComparison.Ordinal))
            return Results.NotFound();
        // #356 — a reset can now lose a concurrency race: disable/enable rotate
        // the target's stamps, so a SetUserPassword racing one of them fails
        // with Users.Conflict rather than a password-policy error. Without this
        // branch that surfaced as a 422 the SPA reads as "password rejected"
        // (codex review of #492 round 2).
        return result.Error.Code.EndsWith(".Conflict", StringComparison.Ordinal)
            ? Results.Problem(result.Error.Description, statusCode: StatusCodes.Status409Conflict, title: result.Error.Code)
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

    private static async Task<IResult> ChangeUserEmail(
        Guid id,
        ChangeUserEmailRequest request,
        [FromHeader(Name = Cluckwork.Api.Endpoints.Auth.AuthEndpoints.StepUpHeaderName)] string? stepUpToken,
        ChangeUserEmailHandler handler,
        IValidator<ChangeUserEmailCommand> validator,
        TenantContext tenant,
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        if (!tenant.IsResolved || !currentUser.IsResolved) return Results.Unauthorized();

        var command = new ChangeUserEmailCommand(id, request.Email, stepUpToken);
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            return ValidationResponse.Problem(validation);

        var result = await handler.HandleAsync(command, tenant.AccountId, currentUser.UserId, ct);
        if (result.IsSuccess) return Results.NoContent();
        if (result.Error.Code is Cluckwork.Application.Common.StepUpErrorCodes.Required or "Auth.Forbidden")
            return Results.Problem(result.Error.Description,
                statusCode: StatusCodes.Status403Forbidden, title: result.Error.Code);
        if (result.Error.Code.EndsWith(".NotFound", StringComparison.Ordinal))
            return Results.NotFound();
        if (result.Error.Code == "Users.DuplicateEmail")
            return Results.Problem(result.Error.Description,
                statusCode: StatusCodes.Status409Conflict, title: result.Error.Code);
        return result.Error.Code.EndsWith(".Conflict", StringComparison.Ordinal)
            ? Results.Problem(result.Error.Description,
                statusCode: StatusCodes.Status409Conflict, title: result.Error.Code)
            : Results.Problem(result.Error.Description, statusCode: 422, title: result.Error.Code);
    }

    private static async Task<IResult> DisableUser(
        Guid id,
        DisableUserRequest? request,
        [FromHeader(Name = Cluckwork.Api.Endpoints.Auth.AuthEndpoints.StepUpHeaderName)] string? stepUpToken,
        DisableUserHandler handler,
        IValidator<DisableUserCommand> validator,
        TenantContext tenant,
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        if (!tenant.IsResolved || !currentUser.IsResolved) return Results.Unauthorized();

        // #356 — same shape as CannotChangeOwnRole/CannotSetOwnPassword. An
        // Owner disabling themselves would revoke their own live session with
        // no re-issue, and a SOLE Owner would lock the farm out of its own
        // account with no email reset path — recoverable only by the offline
        // `recover-admin` verb. Blocked before validation so the guard cannot
        // be reached through a malformed body.
        if (currentUser.UserId == id)
            return Results.Problem(
                "You cannot disable your own account. Ask another Owner.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Users.CannotDisableSelf");

        var command = new DisableUserCommand(id, request?.Reason, stepUpToken);
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            return ValidationResponse.Problem(validation);

        return MapUserStateResult(
            await handler.HandleAsync(command, tenant.AccountId, currentUser.UserId, ct));
    }

    private static async Task<IResult> EnableUser(
        Guid id,
        [FromHeader(Name = Cluckwork.Api.Endpoints.Auth.AuthEndpoints.StepUpHeaderName)] string? stepUpToken,
        EnableUserHandler handler,
        TenantContext tenant,
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        if (!tenant.IsResolved || !currentUser.IsResolved) return Results.Unauthorized();

        // Symmetric with the disable guard. Always a no-op in practice — a
        // disabled caller cannot authenticate at all — but leaving it open
        // would make the self-target rule depend on which verb you picked.
        if (currentUser.UserId == id)
            return Results.Problem(
                "You cannot enable your own account.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Users.CannotEnableSelf");

        return MapUserStateResult(
            await handler.HandleAsync(new EnableUserCommand(id, stepUpToken),
                tenant.AccountId, currentUser.UserId, ct));
    }

    // #356 — shared by both verbs, and identical to ChangeUserRole's mapping:
    // a missing/invalid step-up grant, or an actor no longer an active Owner,
    // is a 403 (authenticated but lacking a required proof of current
    // authorization) — distinct from the 404/409/422s below.
    private static IResult MapUserStateResult(Result result)
    {
        if (result.IsSuccess) return Results.NoContent();
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
        return Results.Ok(users.Select(u =>
            new UserResponse(u.Id, u.Email, u.DisplayName, u.Role, u.DisabledAt)));
    }
}

public sealed record CreateUserRequest(string Email, string Password, string Role, string? Name = null);

public sealed record UpdateUserRequest(string? Name);

public sealed record SetUserPasswordRequest(string NewPassword);

public sealed record ChangeUserRoleRequest(string Role);

public sealed record ChangeUserEmailRequest(string Email);

// #356 — the body is optional: an EMPTY application/json body binds null and
// means "no reason given" (pinned by Disable_WithNoBodyAtAll_Is204). Sending
// no Content-Type at all is not the same thing — the body parameter gives
// this endpoint application/json Accepts metadata, so the consumes matcher
// drops it and Program.cs's `/api/{**rest}` catch-all answers 404 rather than
// 415 (pinned separately by Disable_WithNoContentTypeAtAll_Is404_NotUnsupportedMediaType).
public sealed record DisableUserRequest(string? Reason);

// #356 — DisabledAt is null for an active user, and the SPA renders the row
// muted with a "Disabled" badge when it is set.
public sealed record UserResponse(
    Guid Id, string Email, string? DisplayName, string Role, DateTimeOffset? DisabledAt);

public sealed record AssignFlockRequest(Guid FlockId);

public sealed record FlockAssignmentResponse(Guid Id, Guid? FlockId);
