using System.Security.Cryptography;
using System.Text;
using Cluckwork.Application.Modules.Access.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Cluckwork.Infrastructure.Modules.Access.Identity;

// #1274 "Types": the one owner of two-factor state. Public port, internal
// implementation, as IStepUpGrantRegistry is. Every write method runs inside the
// caller's AmbientTransaction, locks the user row first, and saves what it
// changed; the caller commits, a failed outcome included.
public interface ITwoFactorCredentials
{
    // The one read: untracked, unlocked, by Id and AccountId.
    Task<TwoFactorStatus> GetStatusAsync(Guid accountId, Guid userId, CancellationToken ct);

    Task<FactorOutcome> VerifyAsync(Guid accountId, Guid userId, FactorProof proof, ProofBinding binding, CancellationToken ct);

    // Runs under the resolved tenant of accountId, like ConfirmEnrolmentAsync.
    Task<EnrolmentStart> StartEnrolmentAsync(Guid accountId, Guid userId, CancellationToken ct);

    Task<ConfirmOutcome> ConfirmEnrolmentAsync(
        Guid accountId, Guid userId, SecondFactorCode.Totp code, CancellationToken ct);

    // Null when the user is missing or not enrolled. Replaces every code.
    Task<IReadOnlyList<string>?> RegenerateRecoveryCodesAsync(Guid accountId, Guid userId, CancellationToken ct);

    // Never reads a protected value, so recover-admin runs it under any key ring.
    Task<ClearOutcome> ClearAsync(Guid accountId, Guid userId, CancellationToken ct);
}

public enum ClearOutcome { NotFound, NotEnrolled, PendingDeleted, Cleared }

// #1285 (record 1282 "Checking a passkey"): the one proof VerifyAsync checks,
// a parsed code or a passkey assertion.
[Cluckwork.Application.Common.Credential]
public abstract record FactorProof
{
    private FactorProof() { }

    public sealed record Code(SecondFactorCode Value) : FactorProof;

    // Ceremony is what ConsumeAsync returned, null when it refused it.
    // ExpectedCredentialId is the credential the server looked up at sign-in,
    // and null for a ceremony bound to the user (the second step).
    public sealed record Passkey(ConsumedCeremony? Ceremony, string CredentialJson, byte[]? ExpectedCredentialId, string RequestHost)
        : FactorProof;

    public sealed override string ToString() => "[redacted]";
}

// The credential state the proof was issued against. Login reads it from the
// challenge, with its sf_signins; step-up reads it from its password check,
// with no sign-in count.
public readonly record struct ProofBinding(int CredentialEpoch, string SecurityStampHash, long? SignIns)
{
    public override string ToString() => "[redacted]";

    // The challenge carries this, never the raw stamp, to someone who has
    // proven only the password (decision 1).
    internal static string HashStamp(string? securityStamp) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(securityStamp ?? string.Empty)));
}

public abstract record FactorOutcome
{
    private FactorOutcome() { }

    // RecoveryCodesLeft is set when a recovery code was spent; Passkey when a
    // passkey signed, so the caller saves its sign count (record 1282 decision 7).
    public sealed record Accepted(int? RecoveryCodesLeft, Microsoft.AspNetCore.Identity.UserPasskeyInfo? Passkey = null) : FactorOutcome
    {
        public override string ToString() => "[redacted]";
    }

    // Wrong (charged); reused, locked or a refused passkey (not charged).
    public sealed record Rejected(bool Charged) : FactorOutcome;

    // Disabled, epoch, stamp or sign-in count moved, or two-factor off.
    public sealed record Superseded : FactorOutcome;

    // The key cannot be decrypted; not charged.
    public sealed record Unavailable : FactorOutcome;

    public sealed record Throttled(TimeSpan RetryAfter) : FactorOutcome;
}

public abstract record ConfirmOutcome
{
    private ConfirmOutcome() { }

    // A first enrolment. The codes are plaintext, so the record never prints them.
    public sealed record Enrolled(IReadOnlyList<string> RecoveryCodes) : ConfirmOutcome
    {
        public override string ToString() => "[redacted]";
    }

    public sealed record Replaced : ConfirmOutcome;

    // A wrong code; the wrong-confirm count commits.
    public sealed record Rejected(int AttemptsLeft) : ConfirmOutcome;

    // Missing, expired, rebound or unreadable payload.
    public sealed record Superseded : ConfirmOutcome;
}

public static class TwoFactorCredentialsServiceCollectionExtensions
{
    // Scoped, like the AppDbContext and UserManager it writes through.
    public static IServiceCollection AddTwoFactorCredentials(this IServiceCollection services)
    {
        // Type mappings, not factory delegates, so the MCP scope walk can follow
        // them. It holds no state of its own, so two instances in a scope are fine.
        services.AddScoped<ITwoFactorCredentials, TwoFactorCredentials>();
        services.AddScoped<IOwnerGraceStart, TwoFactorCredentials>();
        return services;
    }
}
