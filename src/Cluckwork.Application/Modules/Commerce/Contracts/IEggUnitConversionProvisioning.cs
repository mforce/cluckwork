namespace Cluckwork.Application.Modules.Commerce.Contracts;

// #1116: Access provisioning's one write into Commerce. Stages a new farm's default unit conversions for the
// resolved tenant into the caller's unit of work, inside the caller's transaction, and never saves; the caller commits.
public interface IEggUnitConversionProvisioning
{
    void StageDefaults();
}
