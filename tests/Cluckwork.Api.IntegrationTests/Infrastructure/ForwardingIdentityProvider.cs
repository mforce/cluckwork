using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Application.Modules.Access.Users;
using Cluckwork.Domain.Modules.Commerce.Contracts;
using Cluckwork.Domain.Common;

namespace Cluckwork.Api.IntegrationTests.Infrastructure;

// Forwards every IIdentityProvider member to the real implementation. A test
// decorator overrides the one member it faults, so a new member is added
// here once rather than in every decorator (#1474 quality review).
internal abstract class ForwardingIdentityProvider(IIdentityProvider inner) : IIdentityProvider
{
    public virtual Task<FarmSignIn?> ResolveFarmCodeAsync(string farmCode, CancellationToken ct = default) => inner.ResolveFarmCodeAsync(farmCode, ct);

    public virtual Task<Result<LoginStep>> LoginAsync(
        Guid accountId, string email, string password, CancellationToken ct = default) =>
        inner.LoginAsync(accountId, email, password, ct);

    public virtual Task<Result<TokenPair>> CompleteSecondFactorLoginAsync(
        string challenge, SecondFactorInput input, CancellationToken ct = default) =>
        inner.CompleteSecondFactorLoginAsync(challenge, input, ct);

    public virtual Task<Result<PasskeyCeremonyStart>> StartSecondFactorPasskeyAsync(
        string? challenge, string requestHost, CancellationToken ct = default) =>
        inner.StartSecondFactorPasskeyAsync(challenge, requestHost, ct);

    public virtual Task<PasskeyCeremonyStart> StartPasskeySignInAsync(string requestHost, CancellationToken ct = default) =>
        inner.StartPasskeySignInAsync(requestHost, ct);

    public virtual Task<Result<PasskeySignIn>> PasskeyLoginAsync(
        Guid ceremony, string credentialJson, string? farmCode, string requestHost, CancellationToken ct = default) =>
        inner.PasskeyLoginAsync(ceremony, credentialJson, farmCode, requestHost, ct);

    public virtual Task<Result<TokenPair>> RefreshAsync(string refreshToken, CancellationToken ct = default, Guid? expectedAccountId = null) =>
        inner.RefreshAsync(refreshToken, ct, expectedAccountId);

    public virtual Task<RefreshTokenRevocationOutcome> RevokeRefreshTokenAsync(
        string refreshToken, CancellationToken ct = default, Guid? expectedAccountId = null) =>
        inner.RevokeRefreshTokenAsync(refreshToken, ct, expectedAccountId);

    public virtual Task RecordLogoutAsync(Guid userId, CancellationToken ct = default) =>
        inner.RecordLogoutAsync(userId, ct);

    public virtual Task<Result<Guid>> CreateUserAsync(
        Guid accountId, string email, string password, string? role,
        string? name = null, bool mustChangePassword = false, CancellationToken ct = default) =>
        inner.CreateUserAsync(accountId, email, password, role, name, mustChangePassword, ct);

    public virtual Task<Result> UpdateUserAsync(Guid accountId, Guid userId, string? name, CancellationToken ct = default) =>
        inner.UpdateUserAsync(accountId, userId, name, ct);

    public virtual Task<Result> SetUserPasswordAsync(
        Guid accountId, Guid userId, string newPassword, CancellationToken ct = default) =>
        inner.SetUserPasswordAsync(accountId, userId, newPassword, ct);

    public virtual Task<Result> ChangeUserRoleAsync(
        Guid accountId, Guid userId, string? role, Guid actingUserId, CancellationToken ct = default) =>
        inner.ChangeUserRoleAsync(accountId, userId, role, actingUserId, ct);

    public virtual Task<Result> ChangeUserEmailAsync(
        Guid accountId, Guid userId, string email, Guid actingUserId, CancellationToken ct = default) =>
        inner.ChangeUserEmailAsync(accountId, userId, email, actingUserId, ct);

    public virtual Task<Result> DisableUserAsync(
        Guid accountId, Guid userId, Guid actingUserId, string? reason, CancellationToken ct = default) =>
        inner.DisableUserAsync(accountId, userId, actingUserId, reason, ct);

    public virtual Task<Result> EnableUserAsync(
        Guid accountId, Guid userId, Guid actingUserId, CancellationToken ct = default) =>
        inner.EnableUserAsync(accountId, userId, actingUserId, ct);

    public virtual Task<Result> BreakGlassResetAsync(
        Guid accountId, Guid userId, string newPassword, string? reason,
        bool twoFactorCleared, int passkeysRemoved, bool mustChangePassword, CancellationToken ct = default) =>
        inner.BreakGlassResetAsync(accountId, userId, newPassword, reason, twoFactorCleared, passkeysRemoved, mustChangePassword, ct);

    public virtual Task<PasskeyCeremonyStart> StartPasskeyRegistrationAsync(
        Guid accountId, Guid userId, string requestHost, CancellationToken ct = default) =>
        inner.StartPasskeyRegistrationAsync(accountId, userId, requestHost, ct);

    public virtual Task<Result<PasskeyRegistration>> RegisterPasskeyAsync(
        Guid accountId, Guid userId, int credentialEpoch, Guid ceremony, string credentialJson, string name,
        string requestHost, CancellationToken ct = default) =>
        inner.RegisterPasskeyAsync(accountId, userId, credentialEpoch, ceremony, credentialJson, name, requestHost, ct);

    public virtual Task<IReadOnlyList<PasskeySummary>> ListPasskeysAsync(Guid accountId, Guid userId, CancellationToken ct = default) =>
        inner.ListPasskeysAsync(accountId, userId, ct);

    public virtual Task<Result> RenamePasskeyAsync(
        Guid accountId, Guid userId, byte[] credentialId, string name, CancellationToken ct = default) =>
        inner.RenamePasskeyAsync(accountId, userId, credentialId, name, ct);

    public virtual Task<Result<TokenPair>> RemovePasskeyAsync(
        Guid accountId, Guid userId, int credentialEpoch, byte[] credentialId, CancellationToken ct = default) =>
        inner.RemovePasskeyAsync(accountId, userId, credentialEpoch, credentialId, ct);

    public virtual Task<Result> RemoveUserPasskeysAsync(
        Guid accountId, Guid userId, Guid actingUserId, CancellationToken ct = default) =>
        inner.RemoveUserPasskeysAsync(accountId, userId, actingUserId, ct);

    public virtual Task<int> ClearPasskeysAsync(Guid accountId, string? reason, CancellationToken ct = default) =>
        inner.ClearPasskeysAsync(accountId, reason, ct);

    public virtual Task<Result<TokenPair?>> TurnOffTwoFactorAsync(
        Guid accountId, Guid userId, int credentialEpoch, CancellationToken ct = default) =>
        inner.TurnOffTwoFactorAsync(accountId, userId, credentialEpoch, ct);

    public virtual Task<TwoFactorStatus> GetTwoFactorStatusAsync(Guid accountId, Guid userId, CancellationToken ct = default) =>
        inner.GetTwoFactorStatusAsync(accountId, userId, ct);

    public virtual Task<Result<EnrolmentStart>> StartTwoFactorEnrolmentAsync(
        Guid accountId, Guid userId, int credentialEpoch, CancellationToken ct = default) =>
        inner.StartTwoFactorEnrolmentAsync(accountId, userId, credentialEpoch, ct);

    public virtual Task<Result<EnrolmentConfirmation>> ConfirmTwoFactorEnrolmentAsync(
        Guid accountId, Guid userId, string? code, CancellationToken ct = default) =>
        inner.ConfirmTwoFactorEnrolmentAsync(accountId, userId, code, ct);

    public virtual Task<Result<IReadOnlyList<string>>> RegenerateRecoveryCodesAsync(
        Guid accountId, Guid userId, int credentialEpoch, CancellationToken ct = default) =>
        inner.RegenerateRecoveryCodesAsync(accountId, userId, credentialEpoch, ct);

    public virtual Task<Result> ResetUserTwoFactorAsync(
        Guid accountId, Guid userId, Guid actingUserId, CancellationToken ct = default) =>
        inner.ResetUserTwoFactorAsync(accountId, userId, actingUserId, ct);

    public virtual Task<Result<TokenPair>> ChangeOwnPasswordAsync(
        Guid accountId, Guid userId, string currentPassword, string newPassword, string? code,
        CancellationToken ct = default) =>
        inner.ChangeOwnPasswordAsync(accountId, userId, currentPassword, newPassword, code, ct);

    public virtual Task<IReadOnlyList<UserSummary>> ListUsersAsync(Guid accountId, CancellationToken ct = default) =>
        inner.ListUsersAsync(accountId, ct);

    public virtual Task<UserProfile?> GetUserAsync(Guid accountId, Guid userId, CancellationToken ct = default) =>
        inner.GetUserAsync(accountId, userId, ct);

    public virtual Task<Result> SetLanguageAsync(Guid accountId, Guid userId, string? language, CancellationToken ct = default) =>
        inner.SetLanguageAsync(accountId, userId, language, ct);

    public virtual Task<Result> SetStepperUnitAsync(Guid accountId, Guid userId, EggUnit? unit, CancellationToken ct = default) =>
        inner.SetStepperUnitAsync(accountId, userId, unit, ct);
}
