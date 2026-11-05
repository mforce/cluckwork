namespace Cluckwork.Application.Modules.Access.Contracts;

// #857 — Access's surface for HTTP adapters: user administration, the
// caller's own profile, flock assignments, and sessions. Each member forwards
// to the existing handler or identity service, so step-up checks, locks, audit
// rows, tokens and errors are theirs, unchanged. Peers use IAccessLookup instead.
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

    Task<Result> ResetUserTwoFactorAsync(
        ResetUserTwoFactorCommand command, Guid accountId, Guid actingUserId, CancellationToken ct);

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

    Task<Result<LoginStep>> LoginAsync(Guid accountId, string email, string password, CancellationToken ct);

    Task<Result<TokenPair>> CompleteSecondFactorLoginAsync(string challenge, string code, CancellationToken ct);

    // Whether the DEFAULT account has an Owner (#283, #361).
    Task<bool> IsFirstRunProvisionedAsync(CancellationToken ct);

    // The LoginFailed security event for a failure decided before any
    // credential check: an unknown farm code or a suspended farm.
    void RecordLoginFailure();

    Task<Result<TokenPair>> RefreshAsync(string refreshToken, Guid? expectedAccountId, CancellationToken ct);

    Task<RefreshTokenRevocationOutcome> RevokeRefreshTokenAsync(
        string refreshToken, Guid? expectedAccountId, CancellationToken ct);

    Task RecordLogoutAsync(Guid userId, CancellationToken ct);

    // The account is the endpoint's tenant.
    Task<Result<TokenPair>> ChangeOwnPasswordAsync(
        ChangeOwnPasswordCommand command, Guid accountId, Guid userId, CancellationToken ct);

    // Null when the user was not enrolled, so no session changed.
    // credentialEpoch is the acting token's claim.
    Task<Result<TokenPair?>> TurnOffTwoFactorAsync(
        Guid accountId, Guid userId, int credentialEpoch, string? stepUpToken, CancellationToken ct);

    // #1277 — the caller's own enrolment. Start and regenerate spend a
    // step-up grant: a password-only grant can only come from an unenrolled
    // user, because an enrolled user's step-up needs a code, and a grant dies
    // with the stamp an enrolment rotates. credentialEpoch is the acting
    // token's claim.
    Task<TwoFactorStatus> GetTwoFactorStatusAsync(Guid accountId, Guid userId, CancellationToken ct);

    Task<Result<EnrolmentStart>> StartTwoFactorEnrolmentAsync(
        Guid accountId, Guid userId, int credentialEpoch, string? stepUpToken, CancellationToken ct);

    Task<Result<EnrolmentConfirmation>> ConfirmTwoFactorEnrolmentAsync(
        Guid accountId, Guid userId, string? code, CancellationToken ct);

    Task<Result<IReadOnlyList<string>>> RegenerateRecoveryCodesAsync(
        Guid accountId, Guid userId, int credentialEpoch, string? stepUpToken, CancellationToken ct);

    // An enrolled user also proves code; anyone else's code is ignored.
    Task<Result<StepUpGrant>> IssueStepUpGrantAsync(
        Guid accountId, Guid userId, string currentPassword, string? code, CancellationToken ct);

    // #798 — spends a step-up grant on a decision outside user administration: approving
    // a connected app. Validates and consumes it exactly as the gated user operations do.
    Task<Result> ConsumeStepUpGrantAsync(
        Guid accountId, Guid userId, string? stepUpToken, CancellationToken ct);

    // #799 — the apps this user has connected, most recent first.
    Task<IReadOnlyList<AppConnection>> ListConnectedAppsAsync(Guid userId, CancellationToken ct);

    // #799 — every connection of every person on this farm, for its Owner.
    Task<IReadOnlyList<AppConnection>> ListFarmConnectedAppsAsync(Guid accountId, CancellationToken ct);

    // #799 — revokes every valid authorization the person holds for the app, and their
    // tokens, so the app is refused on its next request. NotFound when the person is not
    // on this farm or has nothing connected to the app.
    Task<Result> DisconnectAppAsync(Guid accountId, Guid userId, string clientId, CancellationToken ct);

    // Jwt:RefreshTokenDays, the refresh cookie's lifetime. Only this value
    // leaves the JWT options; the signing keys stay in Identity.
    int RefreshTokenLifetimeDays { get; }
}

// #512 T047 — an assignment with its flock's CURRENT name. FlockName is null for
// a farm-wide assignment (FlockId null) and for a flock this caller may not see
// or that no longer resolves; FlockId is kept either way.
public sealed record UserFlockAssignment(Guid Id, Guid? FlockId, string? FlockName);
