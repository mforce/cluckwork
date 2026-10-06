namespace Cluckwork.Application.Modules.EggOperations.Contracts;

// #1116: Access provisioning's one write into Egg Operations. Stages a new farm's default grades for the resolved
// tenant into the caller's unit of work, inside the caller's transaction, and never saves; the caller commits.
public interface IEggGradeProvisioning
{
    void StageDefaults();
}
