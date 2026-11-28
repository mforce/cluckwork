using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Access.Contracts;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Cluckwork.Infrastructure.Modules.Access.Identity;

// #1282 "Types": the one owner of passkey state. Public port, internal
// implementation, as ITwoFactorCredentials. Every write runs in the caller's
// transaction, locks the user row first, then loads the user; the caller
// commits, a refused outcome included. requestHost is the request's host
// without its port: the relying party ID and the origin check read it, and
// nothing here reads the request itself.
public interface IPasskeyCredentials
{
    // A registration ceremony bound to the user, under the resolved tenant.
    Task<PasskeyCeremonyStart> StartRegistrationAsync(Guid accountId, Guid userId, string requestHost, CancellationToken ct);

    // #1285: a sign-in ceremony, issued before any farm or user is known
    // (decision 3), so allowCredentials is empty.
    Task<PasskeyCeremonyStart> StartSignInAsync(string requestHost, CancellationToken ct);

    // #1285: the second sign-in step's ceremony, bound to the challenge's
    // user, whom the caller checked; allowCredentials lists their passkeys.
    Task<PasskeyCeremonyStart> StartSecondStepAsync(ApplicationUser user, string requestHost, CancellationToken ct);

    // #1285 (decision 11): the one lookup made before the farm is known, by the
    // credential JSON's id alone. A routing hint: AssertAsync's signer check
    // decides. Null for an unreadable id or an unknown credential.
    Task<PasskeyOwner?> FindOwnerAsync(string credentialJson, CancellationToken ct);

    // #1285: Identity's handler, then the signer check. Refused unless the
    // handler verified userId and, when credentialId is set (at sign-in, the
    // credential the server looked up), that credential byte for byte.
    Task<AssertionOutcome> AssertAsync(
        ConsumedCeremony ceremony, Guid userId, byte[]? credentialId, string credentialJson, string requestHost,
        CancellationToken ct);

    // #1285 (decision 7): the new sign count and backup state of the passkey
    // that signed, saved after the caller resolved the user's tenant.
    Task RecordUseAsync(UserPasskeyInfo passkey, Guid accountId, Guid userId, CancellationToken ct);

    // DELETE … RETURNING by id, the first statement of the caller's
    // transaction. Null when the ceremony is missing, expired, of another
    // purpose or bound to another user; the row is gone either way.
    Task<ConsumedCeremony?> ConsumeAsync(Guid ceremony, CeremonyPurpose purpose, Guid? userId, CancellationToken ct);

    // Inserts and never updates: a credential id stored for anyone is refused.
    Task<RegisterOutcome> RegisterAsync(
        ConsumedCeremony ceremony, Guid accountId, Guid userId, string credentialJson, string name, string requestHost,
        CancellationToken ct);

    Task<IReadOnlyList<PasskeySummary>> ListAsync(Guid accountId, Guid userId, CancellationToken ct);

    Task<bool> RenameAsync(Guid accountId, Guid userId, byte[] credentialId, string name, CancellationToken ct);

    // False when the user has no such passkey. A removal bumps the epoch and
    // rotates the stamp; the caller revokes.
    Task<bool> RemoveAsync(Guid accountId, Guid userId, byte[] credentialId, CancellationToken ct);

    // Every passkey, without reading Data. Null when the user is not in the
    // farm. A user who had any gets the epoch bump and stamp rotation; the
    // caller revokes.
    Task<int?> RemoveAllAsync(Guid accountId, Guid userId, CancellationToken ct);

    // The users of one farm who have a passkey: clear-passkeys' input.
    Task<IReadOnlyList<Guid>> UsersWithPasskeysAsync(Guid accountId, CancellationToken ct);

    // How many passkeys are stored, in every farm: the startup warning's input.
    Task<int> CountAllAsync(CancellationToken ct);
}

// The consumed passkey_ceremonies row, as DELETE … RETURNING reads it.
// State holds the challenge (G7.7).
[Credential]
public sealed record ConsumedCeremony(Guid? UserId, CeremonyPurpose Purpose, string State, DateTimeOffset ExpiresAt)
{
    public override string ToString() => "[redacted]";
}

// The user and shadow AccountId of the row that holds a credential id. None
// is a secret, but G7.7's walk reads every Passkey type as a credential.
public readonly record struct PasskeyOwner(Guid AccountId, Guid UserId, byte[] CredentialId)
{
    public override string ToString() => "[redacted]";
}

public abstract record AssertionOutcome
{
    private AssertionOutcome() { }

    public sealed record Accepted(Guid UserId, UserPasskeyInfo Passkey) : AssertionOutcome
    {
        public override string ToString() => "[redacted]";
    }

    // Identity's reason is logged at debug level only (decision 12).
    public sealed record Refused : AssertionOutcome;
}

public abstract record RegisterOutcome
{
    private RegisterOutcome() { }

    // FirstFactor: the user had no app and no passkey, so every other session ends.
    public sealed record Registered(PasskeySummary Passkey, bool FirstFactor) : RegisterOutcome;

    public sealed record Refused : RegisterOutcome;

    public sealed record LimitReached : RegisterOutcome;
}

public static class PasskeyCredentialsServiceCollectionExtensions
{
    public static IServiceCollection AddPasskeyCredentials(this IServiceCollection services)
    {
        services.AddScoped<IPasskeyCredentials, PasskeyCredentials>();
        services.AddScoped<IPasskeyCeremonyPurge, PasskeyCeremonyPurge>();
        return services;
    }
}
