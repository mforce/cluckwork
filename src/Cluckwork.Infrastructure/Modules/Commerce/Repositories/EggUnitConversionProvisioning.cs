using Cluckwork.Application.Modules.Commerce.Contracts;
using Cluckwork.Domain.Modules.Commerce.Catalog;
using Cluckwork.Infrastructure.Persistence;

namespace Cluckwork.Infrastructure.Modules.Commerce.Repositories;

// The farm is the resolved tenant, never an argument, so no caller can stage another farm's conversions.
public sealed class EggUnitConversionProvisioning(AppDbContext db, TenantContext tenant) : IEggUnitConversionProvisioning
{
    public void StageDefaults()
    {
        if (!tenant.IsResolved)
            throw new InvalidOperationException("Default unit conversions are staged only for a resolved tenant.");
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Default unit conversions are staged only inside the provisioning transaction.");

        db.EggUnitConversions.AddRange(EggUnitConversion.Defaults(tenant.AccountId));
    }
}
