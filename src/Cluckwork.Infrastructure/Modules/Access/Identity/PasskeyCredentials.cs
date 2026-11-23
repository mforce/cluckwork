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
// commits, a refused outcome included.
public interface IPasskeyCredentials
{
    // Passkeys:Origins is set and this request's host is one origin's host.
    bool IsOn { get; }

    // A registration ceremony bound to the user, under the resolved tenant.
    Task<PasskeyCeremonyStart> StartRegistrationAsync(Guid accountId, Guid userId, CancellationToken ct);

    // DELETE … RETURNING, the first statement of the caller's transaction.
    // Null when the ceremony is missing, expired, of another purpose or bound
    // to another user; the row is gone either way.
    Task<ConsumedCeremony?> ConsumeAsync(Guid ceremony, CeremonyPurpose purpose, Guid? userId, CancellationToken ct);

    Task<RegisterResult> RegisterAsync(
        ConsumedCeremony? ceremony, Guid accountId, Guid userId, string credentialJson, string name, CancellationToken ct);

    Task<PasskeyList> ListAsync(Guid accountId, Guid userId, CancellationToken ct);

    Task<bool> RenameAsync(Guid accountId, Guid userId, byte[] credentialId, string name, CancellationToken ct);

    // Removed bumps the epoch and rotates the stamp; the caller revokes.
    Task<RemoveOutcome> RemoveAsync(Guid accountId, Guid userId, byte[] credentialId, CancellationToken ct);

    // Every passkey, without reading Data. Null when the user is not in the
    // farm. A user who had any gets the epoch bump and stamp rotation; the
    // caller revokes.
    Task<int?> RemoveAllAsync(Guid accountId, Guid userId, CancellationToken ct);

    // The users of one farm who have a passkey: clear-passkeys' input.
    Task<IReadOnlyList<Guid>> UsersWithPasskeysAsync(Guid accountId, CancellationToken ct);

    // How many passkeys are stored, in every farm: the startup warning's input.
    Task<int> CountAllAsync(CancellationToken ct);
}

// State holds the challenge (G7.7).
[Credential]
public sealed record ConsumedCeremony(string State)
{
    public override string ToString() => "[redacted]";
}

public enum RegisterOutcome { Registered, RegisteredFirstFactor, Refused, LimitReached }

public sealed record RegisterResult(RegisterOutcome Outcome, PasskeySummary? Passkey = null);

public enum RemoveOutcome { NotFound, Removed }

public static class PasskeyCredentialsServiceCollectionExtensions
{
    public static IServiceCollection AddPasskeyCredentials(this IServiceCollection services)
    {
        services.AddScoped<IPasskeyCredentials, PasskeyCredentials>();
        services.AddScoped<Application.Modules.Access.Contracts.IPasskeyCeremonyPurge, PasskeyCeremonyPurge>();
        return services;
    }
}

// #1282 "The state it owns": AspNetUserPasskeys, AspNetUsers.PasskeyCount and
// passkey_ceremonies. The only code that calls PasskeyHandler, apart from the
// ceremony sweep (PasskeyCeremonyPurge).
internal sealed class PasskeyCredentials(
    AppDbContext db,
    PasskeyHandler<ApplicationUser> handler,
    UserManager<ApplicationUser> userManager,
    IHttpContextAccessor httpContextAccessor,
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

    private HttpContext Http => httpContextAccessor.HttpContext
        ?? throw new InvalidOperationException("Passkey ceremonies run only inside a request.");

    public bool IsOn => httpContextAccessor.HttpContext is { } http && origins.Value.IsOnFor(http);

    public async Task<PasskeyCeremonyStart> StartRegistrationAsync(Guid accountId, Guid userId, CancellationToken ct)
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
            new PasskeyUserEntity { Id = userId.ToString(), Name = label, DisplayName = label }, Http);
        return new PasskeyCeremonyStart(
            await InsertCeremonyAsync(CeremonyPurpose.Registration, userId,
                options.AttestationState ?? throw new InvalidOperationException("Identity returned no attestation state."), ct),
            options.CreationOptionsJson);
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
        var rows = await db.Database.SqlQuery<ConsumedRow>(
            $"""DELETE FROM passkey_ceremonies WHERE "Id" = {ceremony} AND "Purpose" = {(short)purpose} RETURNING "UserId", "State", "ExpiresAt" """)
            .ToListAsync(ct);
        return rows is [var row] && row.UserId == userId && row.ExpiresAt > timeProvider.GetUtcNow()
            ? new ConsumedCeremony(row.State)
            : null;
    }

    [Credential]
    private sealed record ConsumedRow(Guid? UserId, string State, DateTimeOffset ExpiresAt)
    {
        public override string ToString() => "[redacted]";
    }

    public async Task<RegisterResult> RegisterAsync(
        ConsumedCeremony? ceremony, Guid accountId, Guid userId, string credentialJson, string name, CancellationToken ct)
    {
        if (ceremony is null) return Refused("the ceremony was missing, expired or another user's");

        var user = await UserRowLock.LoadLockedAsync(db, accountId, userId, ct);
        if (user is null) return Refused("the user is not in the farm");
        if (user.PasskeyCount >= MaxPasskeys) return new RegisterResult(RegisterOutcome.LimitReached);

        var attestation = await handler.PerformAttestationAsync(new PasskeyAttestationContext
        {
            CredentialJson = credentialJson,
            AttestationState = ceremony.State,
            HttpContext = Http,
        });
        if (!attestation.Succeeded) return Refused(attestation.Failure.Message);

        var passkey = attestation.Passkey;
        passkey.Name = name;
        // Decision 10: a first factor ends every other session, as enrolment
        // confirm does. A passkey beside the app, or a later one, ends nothing.
        var firstFactor = user.PasskeyCount == 0 && !user.TwoFactorEnabled;
        user.PasskeyCount++;
        if (firstFactor) user.CredentialEpoch++;
        // One save: the row and the count. It rotates only ConcurrencyStamp.
        Saved(await userManager.AddOrUpdatePasskeyAsync(user, passkey));
        if (firstFactor) Saved(await userManager.UpdateSecurityStampAsync(user));

        return new RegisterResult(
            firstFactor ? RegisterOutcome.RegisteredFirstFactor : RegisterOutcome.Registered,
            Summary(passkey.CredentialId, passkey.Name, passkey.CreatedAt, passkey.IsBackedUp, passkey.ClientDataJson));
    }

    private RegisterResult Refused(string reason)
    {
        // Decision 12: Identity's message never leaves a debug log.
        logger.LogDebug("Passkey registration refused: {Reason}", reason);
        securityEvents.PasskeyFailed();
        return new RegisterResult(RegisterOutcome.Refused);
    }

    public async Task<PasskeyList> ListAsync(Guid accountId, Guid userId, CancellationToken ct)
    {
        var rows = await Rows(accountId, userId).AsNoTracking()
            .OrderBy(p => p.Data.CreatedAt)
            .Select(p => new { p.CredentialId, p.Data.Name, p.Data.CreatedAt, p.Data.IsBackedUp, p.Data.ClientDataJson })
            .ToListAsync(ct);
        return new PasskeyList(IsOn,
            rows.Select(r => Summary(r.CredentialId, r.Name, r.CreatedAt, r.IsBackedUp, r.ClientDataJson)).ToList());
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
    public async Task<RemoveOutcome> RemoveAsync(Guid accountId, Guid userId, byte[] credentialId, CancellationToken ct)
    {
        var user = await UserRowLock.LoadLockedAsync(db, accountId, userId, ct);
        if (user is null) return RemoveOutcome.NotFound;

        var deleted = await Rows(accountId, userId).Where(p => p.CredentialId == credentialId).ExecuteDeleteAsync(ct);
        if (deleted == 0) return RemoveOutcome.NotFound;

        user.PasskeyCount -= deleted;
        await EndSessionsAsync(user);
        return RemoveOutcome.Removed;
    }

    public async Task<int?> RemoveAllAsync(Guid accountId, Guid userId, CancellationToken ct)
    {
        var user = await UserRowLock.LoadLockedAsync(db, accountId, userId, ct);
        if (user is null) return null;

        var deleted = await Rows(accountId, userId).ExecuteDeleteAsync(ct);
        if (deleted == 0) return 0;

        user.PasskeyCount = 0;
        await EndSessionsAsync(user);
        return deleted;
    }

    public async Task<IReadOnlyList<Guid>> UsersWithPasskeysAsync(Guid accountId, CancellationToken ct) =>
        await db.UserPasskeys.Where(p => EF.Property<Guid>(p, AccountIdProperty) == accountId)
            .Select(p => p.UserId).Distinct().ToListAsync(ct);

    public Task<int> CountAllAsync(CancellationToken ct) => db.UserPasskeys.CountAsync(ct);

    // Decision 16: a removal is a factor change, so the epoch moves and the
    // stamp rotates in the same save as the count.
    private async Task EndSessionsAsync(ApplicationUser user)
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
            origins.Value.Parsed().Count >= 2 ? OriginOf(clientDataJson) : null);

    private static string? OriginOf(byte[] clientDataJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(clientDataJson);
            return doc.RootElement.TryGetProperty("origin", out var origin) ? origin.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private IQueryable<IdentityUserPasskey<Guid>> Rows(Guid accountId, Guid userId) =>
        db.UserPasskeys.Where(p => p.UserId == userId && EF.Property<Guid>(p, AccountIdProperty) == accountId);
}
