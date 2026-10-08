using Cluckwork.Domain.Auditing;
using Cluckwork.Infrastructure.Jobs;
using Cluckwork.Infrastructure.Modules.Access.Identity;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using OpenIddict.EntityFrameworkCore.Models;

namespace Cluckwork.Infrastructure.Persistence;

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
            typeof(DurableJob),
            typeof(DataProtectionKey),
            typeof(OpenIddictEntityFrameworkCoreApplication<Guid>),
            typeof(OpenIddictEntityFrameworkCoreAuthorization<Guid>),
            typeof(OpenIddictEntityFrameworkCoreScope<Guid>),
            typeof(OpenIddictEntityFrameworkCoreToken<Guid>)
        ]);
}
