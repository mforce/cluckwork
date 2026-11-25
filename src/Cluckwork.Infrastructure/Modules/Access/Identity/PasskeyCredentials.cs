using System.Text.Json;
using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Application.Modules.Farm.Accounts;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    // user; allowCredentials lists their passkeys. Null when the user is gone.
    Task<PasskeyCeremonyStart?> StartSecondStepAsync(Guid accountId, Guid userId, string requestHost, CancellationToken ct);

    // #1285 (decision 11): the one lookup made before the farm is known, by
    // credential id alone. A routing hint: AssertAsync's signer check decides.
    Task<PasskeyOwner?> FindOwnerAsync(byte[] credentialId, CancellationToken ct);

    // #1285: Identity's handler, then the signer check. Refused unless the
    // user the handler verified is the expected one and, when the expected
    // signer names a credential, the credential is that one byte for byte.
    Task<AssertionOutcome> AssertAsync(
        ConsumedCeremony ceremony, ExpectedSigner expected, string credentialJson, string requestHost, CancellationToken ct);

    // #1285 (decision 7): the new sign count and backup state of the passkey
    // that signed, saved after the caller resolved the user's tenant.
    Task RecordUseAsync(UserPasskeyInfo passkey, Guid accountId, Guid userId, CancellationToken ct);

    // DELETE … RETURNING, the first statement of the caller's transaction.
    // Null when the ceremony is missing, expired, of another purpose or bound
    // to another user; the row is gone either way.
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
public sealed record ConsumedCeremony(Guid? UserId, string State, DateTimeOffset ExpiresAt)
{
    public override string ToString() => "[redacted]";
}

// The user and shadow AccountId of the row that holds a credential id. None
// is a secret, but G7.7's walk reads every Passkey type as a credential.
public readonly record struct PasskeyOwner(Guid AccountId, Guid UserId, byte[] CredentialId)
{
    public override string ToString() => "[redacted]";
}

// Who must have signed: the user, and at sign-in the exact credential the
// server looked up.
public readonly record struct ExpectedSigner(Guid UserId, byte[]? CredentialId)
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

    // Reason is Identity's failure message, logged at debug level only (decision 12).
    public sealed record Refused(string Reason) : AssertionOutcome;
}

// What VerifyAsync needs beside a SecondFactorCode.Passkey: the ceremony
// ConsumeAsync returned (null when it refused it), the credential the server
// looked up at sign-in (null at the second step), and the request's host.
[Credential]
public sealed record PasskeyAssertion(ConsumedCeremony? Ceremony, byte[]? CredentialId, string RequestHost)
{
    public override string ToString() => "[redacted]";
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

// #1282 "The state it owns": AspNetUserPasskeys, AspNetUsers.PasskeyCount and
// passkey_ceremonies. The only code that calls PasskeyHandler or touches these,
// apart from the handler's registration and the ceremony sweep
// (PasskeyOwnershipTests.OutsideOwner lists them).
internal sealed class PasskeyCredentials(
    AppDbContext db,
    PasskeyHandler<ApplicationUser> handler,
    UserManager<ApplicationUser> userManager,
    IOptions<PasskeyOptions> origins,
    IOptions<IdentityPasskeyOptions> passkeyOptions,
    IAccountRepository accounts,
    TimeProvider timeProvider,
    AuthSecurityEventLogger securityEvents,
    ILogger<PasskeyCredentials> logger) : IPasskeyCredentials
{
    // Decision 6: a constant; the check constraint holds the same bound.
    internal const int MaxPasskeys = 5;

    private const string AccountIdProperty = nameof(ApplicationUser.AccountId);

    // Identity's handler reads only Request.Host.Host from the context it is
    // given (record 1282 "What Identity 10.0.15 does"), and the origin hook
    // reads the same. The route reads Request.Host.Host, which is already
    // decoded, and the context built here gives it back unchanged. The owner
    // takes no IHttpContextAccessor (McpSecondaryScopeTests walks
    // constructors). No method takes the request either, by convention only:
    // IdentityProvider holds the accessor and no guard stops it passing the
    // context here.
    private static HttpContext ForHost(string requestHost) =>
        new DefaultHttpContext { Request = { Host = new HostString(requestHost) } };

    public async Task<PasskeyCeremonyStart> StartRegistrationAsync(Guid accountId, Guid userId, string requestHost, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId && u.AccountId == accountId)
            .Select(u => new { u.Email })
            .SingleAsync(ct);
        var account = await accounts.GetCurrentAsync(ct)
            ?? throw new InvalidOperationException("Registration starts only under a resolved farm.");

        // The phone shows "email (farm name)" for both names, fixed here
        // (decision 5). The user handle is the user id, as FindByIdAsync takes it.
        var label = $"{user.Email} ({account.Name})";
        var options = await handler.MakeCreationOptionsAsync(
            new PasskeyUserEntity { Id = userId.ToString(), Name = label, DisplayName = label }, ForHost(requestHost));
        return new PasskeyCeremonyStart(
            await InsertCeremonyAsync(CeremonyPurpose.Registration, userId,
                options.AttestationState ?? throw new InvalidOperationException("Identity returned no attestation state."), ct),
            options.CreationOptionsJson);
    }

    public async Task<PasskeyCeremonyStart> StartSignInAsync(string requestHost, CancellationToken ct) =>
        await StartAssertionAsync(CeremonyPurpose.SignIn, user: null, requestHost, ct);

    public async Task<PasskeyCeremonyStart?> StartSecondStepAsync(Guid accountId, Guid userId, string requestHost, CancellationToken ct) =>
        await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId && u.AccountId == accountId, ct) is { } user
            ? await StartAssertionAsync(CeremonyPurpose.SecondStep, user, requestHost, ct)
            : null;

    // A bound ceremony's state names the user, so Identity checks the
    // credential against them; the row carries their id for ConsumeAsync.
    private async Task<PasskeyCeremonyStart> StartAssertionAsync(
        CeremonyPurpose purpose, ApplicationUser? user, string requestHost, CancellationToken ct)
    {
        var options = await handler.MakeRequestOptionsAsync(user, ForHost(requestHost));
        return new PasskeyCeremonyStart(
            await InsertCeremonyAsync(purpose, user?.Id,
                options.AssertionState ?? throw new InvalidOperationException("Identity returned no assertion state."), ct),
            options.RequestOptionsJson);
    }

    public async Task<PasskeyOwner?> FindOwnerAsync(byte[] credentialId, CancellationToken ct) =>
        await db.UserPasskeys.AsNoTracking()
            .Where(p => p.CredentialId == credentialId)
            .Select(p => new { AccountId = EF.Property<Guid>(p, AccountIdProperty), p.UserId })
            .SingleOrDefaultAsync(ct) is { } row
            ? new PasskeyOwner(row.AccountId, row.UserId, credentialId)
            : null;

    public async Task<AssertionOutcome> AssertAsync(
        ConsumedCeremony ceremony, ExpectedSigner expected, string credentialJson, string requestHost, CancellationToken ct)
    {
        var assertion = await handler.PerformAssertionAsync(new PasskeyAssertionContext
        {
            CredentialJson = credentialJson,
            AssertionState = ceremony.State,
            HttpContext = ForHost(requestHost),
        });
        if (!assertion.Succeeded) return AssertionRefused(assertion.Failure.Message);

        // Decision 11: Identity takes the user from userHandle and the
        // credential from the JSON's id, and never reads rawId, so the lookup
        // and the signature can disagree; disagreement is a refusal.
        if (assertion.User.Id != expected.UserId) return AssertionRefused("another user signed");
        if (expected.CredentialId is { } credentialId && !assertion.Passkey.CredentialId.AsSpan().SequenceEqual(credentialId))
            return AssertionRefused("another credential signed");
        return new AssertionOutcome.Accepted(assertion.User.Id, assertion.Passkey);
    }

    private AssertionOutcome AssertionRefused(string reason)
    {
        logger.LogDebug("Passkey assertion refused: {Reason}", reason);
        return new AssertionOutcome.Refused(reason);
    }

    public async Task RecordUseAsync(UserPasskeyInfo passkey, Guid accountId, Guid userId, CancellationToken ct)
    {
        var row = await Rows(accountId, userId).SingleAsync(p => p.CredentialId == passkey.CredentialId, ct);
        row.Data.SignCount = passkey.SignCount;
        row.Data.IsBackedUp = passkey.IsBackedUp;
        await db.SaveChangesAsync(ct);
    }

    private async Task<Guid> InsertCeremonyAsync(CeremonyPurpose purpose, Guid? userId, string state, CancellationToken ct)
    {
        var ceremony = new PasskeyCeremony
        {
            Id = Guid.NewGuid(),
            Purpose = purpose,
            UserId = userId,
            State = state,
            ExpiresAt = timeProvider.GetUtcNow() + passkeyOptions.Value.AuthenticatorTimeout,
        };
        db.PasskeyCeremonies.Add(ceremony);
        await db.SaveChangesAsync(ct);
        return ceremony.Id;
    }

    // Decision 3: one statement deletes the row, so two finishes of one
    // ceremony consume it once, and every refusal after it commits the delete.
    public async Task<ConsumedCeremony?> ConsumeAsync(
        Guid ceremony, CeremonyPurpose purpose, Guid? userId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<ConsumedCeremony>(
            $"""DELETE FROM passkey_ceremonies WHERE "Id" = {ceremony} AND "Purpose" = {(short)purpose} RETURNING "UserId", "State", "ExpiresAt" """)
            .ToListAsync(ct);
        return rows is [var row] && row.UserId == userId && row.ExpiresAt > timeProvider.GetUtcNow() ? row : null;
    }

    public async Task<RegisterOutcome> RegisterAsync(
        ConsumedCeremony ceremony, Guid accountId, Guid userId, string credentialJson, string name, string requestHost,
        CancellationToken ct)
    {
        // Postgres refuses \u0000 in the jsonb Data column, which the
        // credential's transports reach. The check refuses the whole
        // credential once, here, after the ceremony is consumed, rather than
        // field by field.
        if (credentialJson.Contains(@"\u0000", StringComparison.Ordinal)) return Refused("the credential holds a NUL character");

        var user = await UserRowLock.LoadLockedAsync(db, accountId, userId, ct);
        if (user is null) return Refused("the user is not in the farm");
        // G7.4: a locked user is refused at every step, this finish included.
        if (await userManager.IsLockedOutAsync(user)) return Refused("the user is locked out");
        if (user.PasskeyCount >= MaxPasskeys) return new RegisterOutcome.LimitReached();

        var attestation = await handler.PerformAttestationAsync(new PasskeyAttestationContext
        {
            CredentialJson = credentialJson,
            AttestationState = ceremony.State,
            HttpContext = ForHost(requestHost),
        });
        if (!attestation.Succeeded) return Refused(attestation.Failure.Message);

        // An insert, never Identity's add-or-update: the handler's duplicate
        // check runs before this write, so a credential id that someone else
        // stored in between would otherwise update their row. The key refuses
        // it instead. EF wraps SaveChanges in its own savepoint inside the
        // caller's transaction, so the failed insert rolls back alone and the
        // consumed ceremony still commits.
        var passkey = attestation.Passkey;
        var row = new IdentityUserPasskey<Guid>
        {
            CredentialId = passkey.CredentialId,
            UserId = user.Id,
            Data = new IdentityPasskeyData
            {
                PublicKey = passkey.PublicKey,
                Name = name,
                CreatedAt = passkey.CreatedAt,
                SignCount = passkey.SignCount,
                Transports = passkey.Transports,
                IsUserVerified = passkey.IsUserVerified,
                IsBackupEligible = passkey.IsBackupEligible,
                IsBackedUp = passkey.IsBackedUp,
                AttestationObject = passkey.AttestationObject,
                ClientDataJson = passkey.ClientDataJson,
            },
        };
        // Decision 10: a first factor ends every other session, as enrolment
        // confirm does. A passkey beside the app, or a later one, ends nothing.
        var firstFactor = user.PasskeyCount == 0 && !user.TwoFactorEnabled;
        db.UserPasskeys.Add(row);
        user.PasskeyCount++;
        try
        {
            // One save: the row and the count, and for a first factor the epoch and stamp.
            if (firstFactor) await MarkFactorChangedAsync(user);
            else Saved(await userManager.UpdateAsync(user));
        }
        catch (DbUpdateException e) when (e.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            return Refused("the credential is already registered");
        }

        return new RegisterOutcome.Registered(
            Summary(row.CredentialId, name, row.Data.CreatedAt, row.Data.IsBackedUp, row.Data.ClientDataJson), firstFactor);
    }

    // Decision 12: Identity's message never leaves a debug log.
    private RegisterOutcome Refused(string reason)
    {
        logger.LogDebug("Passkey registration refused: {Reason}", reason);
        securityEvents.PasskeyFailed();
        return new RegisterOutcome.Refused();
    }

    public async Task<IReadOnlyList<PasskeySummary>> ListAsync(Guid accountId, Guid userId, CancellationToken ct)
    {
        var rows = await Rows(accountId, userId).AsNoTracking()
            .OrderBy(p => p.Data.CreatedAt)
            .Select(p => new { p.CredentialId, p.Data.Name, p.Data.CreatedAt, p.Data.IsBackedUp, p.Data.ClientDataJson })
            .ToListAsync(ct);
        return [.. rows.Select(r => Summary(r.CredentialId, r.Name, r.CreatedAt, r.IsBackedUp, r.ClientDataJson))];
    }

    public async Task<bool> RenameAsync(Guid accountId, Guid userId, byte[] credentialId, string name, CancellationToken ct)
    {
        if (await UserRowLock.LoadLockedAsync(db, accountId, userId, ct) is null) return false;
        var row = await Rows(accountId, userId).SingleOrDefaultAsync(p => p.CredentialId == credentialId, ct);
        if (row is null) return false;

        row.Data.Name = name;
        await db.SaveChangesAsync(ct);
        return true;
    }

    // Decision 10: the row is found by user, farm and credential id under the
    // lock, and the count drops by the rows actually deleted (G7.13).
    public async Task<bool> RemoveAsync(Guid accountId, Guid userId, byte[] credentialId, CancellationToken ct)
    {
        var user = await UserRowLock.LoadLockedAsync(db, accountId, userId, ct);
        if (user is null) return false;

        var deleted = await Rows(accountId, userId).Where(p => p.CredentialId == credentialId).ExecuteDeleteAsync(ct);
        if (deleted == 0) return false;

        user.PasskeyCount -= deleted;
        await MarkFactorChangedAsync(user);
        return true;
    }

    public async Task<int?> RemoveAllAsync(Guid accountId, Guid userId, CancellationToken ct)
    {
        var user = await UserRowLock.LoadLockedAsync(db, accountId, userId, ct);
        if (user is null) return null;

        var deleted = await Rows(accountId, userId).ExecuteDeleteAsync(ct);
        if (deleted == 0) return 0;

        user.PasskeyCount = 0;
        await MarkFactorChangedAsync(user);
        return deleted;
    }

    public async Task<IReadOnlyList<Guid>> UsersWithPasskeysAsync(Guid accountId, CancellationToken ct) =>
        await db.UserPasskeys.Where(p => EF.Property<Guid>(p, AccountIdProperty) == accountId)
            .Select(p => p.UserId).Distinct().ToListAsync(ct);

    public Task<int> CountAllAsync(CancellationToken ct) => db.UserPasskeys.CountAsync(ct);

    // Decision 16: a first passkey and a removal are factor changes, so the
    // epoch moves and the stamp rotates in the same save as the count. The
    // stamp is what ends earlier step-up grants; the caller revokes sessions.
    private async Task MarkFactorChangedAsync(ApplicationUser user)
    {
        user.CredentialEpoch++;
        Saved(await userManager.UpdateSecurityStampAsync(user));
    }

    private static void Saved(IdentityResult result)
    {
        if (!result.Succeeded)
            throw new InvalidOperationException("The locked user's save failed: " + string.Join(" ", result.Errors.Select(e => e.Code)));
    }

    // Decision 14: the address only when two or more origins are configured,
    // read from the registration's client data.
    private PasskeySummary Summary(byte[] credentialId, string? name, DateTimeOffset createdAt, bool backedUp, byte[] clientDataJson) =>
        new(WebEncoders.Base64UrlEncode(credentialId), name, createdAt, backedUp,
            origins.Value.Parsed.Count >= 2 ? OriginOf(clientDataJson) : null);

    // Identity checked this client data at registration.
    private static string? OriginOf(byte[] clientDataJson)
    {
        using var doc = JsonDocument.Parse(clientDataJson);
        return doc.RootElement.TryGetProperty("origin", out var origin) ? origin.GetString() : null;
    }

    private IQueryable<IdentityUserPasskey<Guid>> Rows(Guid accountId, Guid userId) =>
        db.UserPasskeys.Where(p => p.UserId == userId && EF.Property<Guid>(p, AccountIdProperty) == accountId);
}
