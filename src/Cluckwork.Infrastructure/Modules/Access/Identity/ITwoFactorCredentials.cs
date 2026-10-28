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
    Task<FactorOutcome> VerifyAsync(
        Guid accountId, Guid userId, SecondFactorCode code, ProofBinding binding, CancellationToken ct);

    // Runs under the resolved tenant of accountId, like ConfirmEnrolmentAsync.
    Task<EnrolmentStart> StartEnrolmentAsync(Guid accountId, Guid userId, CancellationToken ct);

    Task<ConfirmOutcome> ConfirmEnrolmentAsync(
        Guid accountId, Guid userId, SecondFactorCode.Totp code, CancellationToken ct);
}

public sealed record EnrolmentStart(string Key, string OtpauthUri)
{
    public override string ToString() => "[redacted]";
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

    // RecoveryCodesLeft is set when a recovery code was spent.
    public sealed record Accepted(int? RecoveryCodesLeft) : FactorOutcome;

    // Wrong (charged); reused or locked (not charged).
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

    // A first enrolment.
    public sealed record Enrolled(IReadOnlyList<string> RecoveryCodes) : ConfirmOutcome;

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
        services.AddScoped<TwoFactorCredentials>();
        services.AddScoped<ITwoFactorCredentials>(sp => sp.GetRequiredService<TwoFactorCredentials>());
        return services;
    }
}
