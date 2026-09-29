namespace Cluckwork.Infrastructure.Persistence;

using Cluckwork.Domain.Auditing;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Jobs;
using Microsoft.AspNetCore.Identity;

internal static class PlatformBusinessRecords
{
    public static readonly BusinessRecordContribution Contribution = new(
        "Platform",
        [],
        [
            typeof(AuditEvent),
            typeof(ApplicationRole),
            typeof(IdentityRoleClaim<Guid>),
            typeof(IdentityUserClaim<Guid>),
            typeof(IdentityUserLogin<Guid>),
            typeof(IdentityUserRole<Guid>),
            typeof(IdentityUserToken<Guid>),
            typeof(IdentityPasskeyData),
            typeof(RefreshToken),
            typeof(IdempotencyRecord),
            typeof(SimulationSeedState),
            typeof(DurableJob)
        ]);
}
