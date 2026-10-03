using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Users;
using Cluckwork.Application.Features.Users.AssignFlock;
using Cluckwork.Application.Features.Users.ChangeOwnPassword;
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
using Microsoft.Extensions.Options;

namespace Cluckwork.Infrastructure.Identity;

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
    IUserRoleAssignmentRepository assignments) : IAccessModule
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

    public Task<IReadOnlyList<UserFlockAssignment>> ListFlockAssignmentsAsync(Guid userId, CancellationToken ct) =>
        assignments.ListByNameByUserAsync(userId, ct);

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

    public int RefreshTokenLifetimeDays => jwt.Value.RefreshTokenDays;
}
