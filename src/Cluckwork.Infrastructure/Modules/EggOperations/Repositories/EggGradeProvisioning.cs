using Cluckwork.Application.Modules.EggOperations.Contracts;
using Cluckwork.Domain.Modules.EggOperations.Eggs;
using Cluckwork.Domain.Modules.Farm.Contracts;
using Cluckwork.Infrastructure.Persistence;

namespace Cluckwork.Infrastructure.Modules.EggOperations.Repositories;

// The farm is the resolved tenant, never an argument, so no caller can stage another farm's grades.
public sealed class EggGradeProvisioning(AppDbContext db, TenantContext tenant) : IEggGradeProvisioning
{
    public void StageDefaults()
    {
        if (!tenant.IsResolved)
            throw new InvalidOperationException("Default egg grades are staged only for a resolved tenant.");
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Default egg grades are staged only inside the provisioning transaction.");

        db.EggGrades.AddRange(EggGrade.Defaults(tenant.AccountId, SeedDefaults.FarmId));
    }
}
