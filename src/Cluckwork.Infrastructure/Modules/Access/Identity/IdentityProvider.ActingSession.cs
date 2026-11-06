using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Infrastructure.Modules.Access.Identity;

// #1280, #1277: turn-off, start and regenerate spend their step-up grant before
// they take the user-row lock, so a password reset, an enrolment or a disable
// can land in between. Each bumps the epoch, so the session that asked is then
// stale: it must not change the factor or receive a key or codes.
public sealed partial class IdentityProvider
{
    private static readonly Error CredentialsSuperseded =
        new(CredentialErrorCodes.Superseded, "Your credentials have been superseded. Sign in again.");

    private async Task<bool> ActingSessionIsCurrentAsync(
        Guid accountId, Guid userId, int credentialEpoch, CancellationToken ct)
    {
        await UserRowLock.TakeAsync(db, accountId, userId, ct);
        var epoch = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId && u.AccountId == accountId)
            .Select(u => (int?)u.CredentialEpoch)
            .SingleOrDefaultAsync(ct);
        return epoch == credentialEpoch;
    }
}
