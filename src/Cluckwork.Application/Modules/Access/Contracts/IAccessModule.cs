using Cluckwork.Application.Common;
using Cluckwork.Domain.Common;

namespace Cluckwork.Application.Modules.Access.Contracts;

// #857 — Access's surface for HTTP adapters: user administration, the
// caller's own profile, flock assignments, and sessions. Each member forwards
// to the existing handler or identity service, so step-up checks, locks, audit
// rows, tokens and errors are theirs, unchanged. Peers use IAccessLookup instead.
[ModuleContract("Access")]
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

    Task<FarmSignIn?> ResolveFarmCodeAsync(string farmCode, CancellationToken ct);

    Task<Result<TokenPair>> LoginAsync(Guid accountId, string email, string password, CancellationToken ct);

    // Whether the DEFAULT account has an Owner (#283, #361).
    Task<bool> IsFirstRunProvisionedAsync(CancellationToken ct);

    // The LoginFailed security event for a failure decided before any
    // credential check: an unknown farm code or a suspended farm.
    void RecordLoginFailure();

    Task<Result<TokenPair>> RefreshAsync(string refreshToken, Guid? expectedAccountId, CancellationToken ct);

    Task<RefreshTokenRevocationOutcome> RevokeRefreshTokenAsync(
        string refreshToken, Guid? expectedAccountId, CancellationToken ct);

    Task RecordLogoutAsync(Guid userId, CancellationToken ct);

    Task<Result<TokenPair>> ChangeOwnPasswordAsync(ChangeOwnPasswordCommand command, Guid userId, CancellationToken ct);

    Task<Result<StepUpGrant>> IssueStepUpGrantAsync(
        Guid accountId, Guid userId, string currentPassword, CancellationToken ct);

    // Jwt:RefreshTokenDays, the refresh cookie's lifetime. Only this value
    // leaves the JWT options; the signing keys stay in Identity.
    int RefreshTokenLifetimeDays { get; }
}

// #512 T047 — an assignment with its flock's CURRENT name. FlockName is null for
// a farm-wide assignment (FlockId null) and for a flock this caller may not see
// or that no longer resolves; FlockId is kept either way.
[ModuleContract("Access")]
public sealed record UserFlockAssignment(Guid Id, Guid? FlockId, string? FlockName);
