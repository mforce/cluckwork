using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Application.Modules.Access.Users;
using Cluckwork.Application.Modules.Access.Users.AssignFlock;
using Cluckwork.Application.Modules.Access.Users.ChangeOwnPassword;
using Cluckwork.Application.Modules.Access.Users.ChangeUserEmail;
using Cluckwork.Application.Modules.Access.Users.ChangeUserRole;
using Cluckwork.Application.Modules.Access.Users.CreateUser;
using Cluckwork.Application.Modules.Access.Users.DisableUser;
using Cluckwork.Application.Modules.Access.Users.EnableUser;
using Cluckwork.Application.Modules.Access.Users.SetLanguage;
using Cluckwork.Application.Modules.Access.Users.SetStepperUnit;
using Cluckwork.Application.Modules.Access.Users.SetUserPassword;
using Cluckwork.Application.Modules.Access.Users.UpdateUser;
using Cluckwork.Application.Modules.FlockManagement.Contracts;
using Cluckwork.Domain.Common;
using Microsoft.Extensions.Options;

namespace Cluckwork.Infrastructure.Modules.Access.Identity;

// #857 — lives in Infrastructure because the session members read Identity
// configuration and services. It takes IIdentityProvider from DI, never
// the concrete provider, so a registered decorator stays in the path.
public sealed class AccessModule(
    CreateUserHandler createUser,
    UpdateUserHandler updateUser,
    SetUserPasswordHandler setUserPassword,
    ChangeUserRoleHandler changeUserRole,
    ChangeUserEmailHandler changeUserEmail,
    DisableUserHandler disableUser,
    EnableUserHandler enableUser,
    AssignFlockHandler assignFlock,
    UnassignFlockHandler unassignFlock,
    SetLanguageHandler setLanguage,
    SetStepperUnitHandler setStepperUnit,
    ChangeOwnPasswordHandler changeOwnPassword,
    IIdentityProvider identity,
    IStepUpGrantService stepUp,
    FirstRunStatusService firstRun,
    AuthSecurityEventLogger securityEvents,
    IOptions<JwtOptions> jwt,
    IUserRoleAssignmentRepository assignments,
    IFlockLookup flocks) : IAccessModule
{
    public Task<Result<Guid>> CreateUserAsync(
        CreateUserCommand command, Guid accountId, Guid actingUserId, CancellationToken ct) =>
        createUser.HandleAsync(command, accountId, actingUserId, ct);

    public Task<IReadOnlyList<UserSummary>> ListUsersAsync(Guid accountId, CancellationToken ct) =>
        identity.ListUsersAsync(accountId, ct);

    public Task<Result> UpdateUserAsync(UpdateUserCommand command, Guid accountId, CancellationToken ct) =>
        updateUser.HandleAsync(command, accountId, ct);

    public Task<Result> SetUserPasswordAsync(
        SetUserPasswordCommand command, Guid accountId, Guid actingUserId, CancellationToken ct) =>
        setUserPassword.HandleAsync(command, accountId, actingUserId, ct);

    public Task<Result> ChangeUserRoleAsync(
        ChangeUserRoleCommand command, Guid accountId, Guid actingUserId, CancellationToken ct) =>
        changeUserRole.HandleAsync(command, accountId, actingUserId, ct);

    public Task<Result> ChangeUserEmailAsync(
        ChangeUserEmailCommand command, Guid accountId, Guid actingUserId, CancellationToken ct) =>
        changeUserEmail.HandleAsync(command, accountId, actingUserId, ct);

    public Task<Result> DisableUserAsync(
        DisableUserCommand command, Guid accountId, Guid actingUserId, CancellationToken ct) =>
        disableUser.HandleAsync(command, accountId, actingUserId, ct);

    public Task<Result> EnableUserAsync(
        EnableUserCommand command, Guid accountId, Guid actingUserId, CancellationToken ct) =>
        enableUser.HandleAsync(command, accountId, actingUserId, ct);

    // #859 — names come from Flock Management's lookup, whose filtered read
    // decides which flocks this caller may see (#613): one bounded read for the
    // whole list, after the assignment read.
    public async Task<IReadOnlyList<UserFlockAssignment>> ListFlockAssignmentsAsync(Guid userId, CancellationToken ct)
    {
        var rows = await assignments.ListByUserAsync(userId, ct);
        var names = await flocks.GetDisplayNamesAsync(rows.Select(a => a.FlockId).OfType<Guid>().ToList(), ct);
        return rows.Select(a => new UserFlockAssignment(a.Id, a.FlockId,
            a.FlockId is { } id && names.TryGetValue(id, out var flock) ? flock.Name : null)).ToList();
    }

    public Task<Result<Guid>> AssignFlockAsync(
        AssignFlockCommand command, Guid accountId, Guid actingUserId, CancellationToken ct) =>
        assignFlock.HandleAsync(command, accountId, actingUserId, ct);

    public Task<Result> UnassignFlockAsync(
        UnassignFlockCommand command, Guid accountId, Guid actingUserId, CancellationToken ct) =>
        unassignFlock.HandleAsync(command, accountId, actingUserId, ct);

    public Task<UserProfile?> GetUserAsync(Guid accountId, Guid userId, CancellationToken ct) =>
        identity.GetUserAsync(accountId, userId, ct);

    public Task<Result> SetLanguageAsync(
        SetLanguageCommand command, Guid accountId, Guid userId, CancellationToken ct) =>
        setLanguage.HandleAsync(command, accountId, userId, ct);

    public Task<Result> SetStepperUnitAsync(
        SetStepperUnitCommand command, Guid accountId, Guid userId, CancellationToken ct) =>
        setStepperUnit.HandleAsync(command, accountId, userId, ct);

    public Task<FarmSignIn?> ResolveFarmCodeAsync(string farmCode, CancellationToken ct) =>
        identity.ResolveFarmCodeAsync(farmCode, ct);

    public Task<Result<TokenPair>> LoginAsync(Guid accountId, string email, string password, CancellationToken ct) =>
        identity.LoginAsync(accountId, email, password, ct);

    public Task<bool> IsFirstRunProvisionedAsync(CancellationToken ct) => firstRun.IsProvisionedAsync(ct);

    public void RecordLoginFailure() => securityEvents.LoginFailed();

    public Task<Result<TokenPair>> RefreshAsync(string refreshToken, Guid? expectedAccountId, CancellationToken ct) =>
        identity.RefreshAsync(refreshToken, ct, expectedAccountId);

    public Task<RefreshTokenRevocationOutcome> RevokeRefreshTokenAsync(
        string refreshToken, Guid? expectedAccountId, CancellationToken ct) =>
        identity.RevokeRefreshTokenAsync(refreshToken, ct, expectedAccountId);

    public Task RecordLogoutAsync(Guid userId, CancellationToken ct) => identity.RecordLogoutAsync(userId, ct);

    public Task<Result<TokenPair>> ChangeOwnPasswordAsync(
        ChangeOwnPasswordCommand command, Guid userId, CancellationToken ct) =>
        changeOwnPassword.HandleAsync(command, userId, ct);

    public Task<Result<StepUpGrant>> IssueStepUpGrantAsync(
        Guid accountId, Guid userId, string currentPassword, CancellationToken ct) =>
        stepUp.IssueAsync(accountId, userId, currentPassword, ct);

    public Task<Result> ConsumeStepUpGrantAsync(
        Guid accountId, Guid userId, string? stepUpToken, CancellationToken ct) =>
        stepUp.ValidateAsync(accountId, userId, stepUpToken, ct);

    public int RefreshTokenLifetimeDays => jwt.Value.RefreshTokenDays;
}
