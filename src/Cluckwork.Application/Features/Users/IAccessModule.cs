using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Users.AssignFlock;
using Cluckwork.Application.Features.Users.ChangeUserEmail;
using Cluckwork.Application.Features.Users.ChangeUserRole;
using Cluckwork.Application.Features.Users.CreateUser;
using Cluckwork.Application.Features.Users.DisableUser;
using Cluckwork.Application.Features.Users.EnableUser;
using Cluckwork.Application.Features.Users.SetLanguage;
using Cluckwork.Application.Features.Users.SetStepperUnit;
using Cluckwork.Application.Features.Users.SetUserPassword;
using Cluckwork.Application.Features.Users.UpdateUser;
using Cluckwork.Domain.Common;

namespace Cluckwork.Application.Features.Users;

// #857 — Access's surface for HTTP adapters: user administration, the
// caller's own profile, and flock assignments. Each member forwards to the
// existing handler or identity call, so step-up checks, locks, audit rows and
// errors are theirs, unchanged. Peers use IAccessLookup instead.
public interface IAccessModule
{
    Task<Result<Guid>> CreateUserAsync(
        CreateUserCommand command, Guid accountId, Guid actingUserId, CancellationToken ct);

    Task<IReadOnlyList<UserSummary>> ListUsersAsync(Guid accountId, CancellationToken ct);

    Task<Result> UpdateUserAsync(UpdateUserCommand command, Guid accountId, CancellationToken ct);

    Task<Result> SetUserPasswordAsync(
        SetUserPasswordCommand command, Guid accountId, Guid actingUserId, CancellationToken ct);

    Task<Result> ChangeUserRoleAsync(
        ChangeUserRoleCommand command, Guid accountId, Guid actingUserId, CancellationToken ct);

    Task<Result> ChangeUserEmailAsync(
        ChangeUserEmailCommand command, Guid accountId, Guid actingUserId, CancellationToken ct);

    Task<Result> DisableUserAsync(
        DisableUserCommand command, Guid accountId, Guid actingUserId, CancellationToken ct);

    Task<Result> EnableUserAsync(
        EnableUserCommand command, Guid accountId, Guid actingUserId, CancellationToken ct);

    Task<IReadOnlyList<UserFlockAssignment>> ListFlockAssignmentsAsync(Guid userId, CancellationToken ct);

    Task<Result<Guid>> AssignFlockAsync(
        AssignFlockCommand command, Guid accountId, Guid actingUserId, CancellationToken ct);

    Task<Result> UnassignFlockAsync(
        UnassignFlockCommand command, Guid accountId, Guid actingUserId, CancellationToken ct);

    Task<UserProfile?> GetUserAsync(Guid accountId, Guid userId, CancellationToken ct);

    Task<Result> SetLanguageAsync(
        SetLanguageCommand command, Guid accountId, Guid userId, CancellationToken ct);

    Task<Result> SetStepperUnitAsync(
        SetStepperUnitCommand command, Guid accountId, Guid userId, CancellationToken ct);
}
