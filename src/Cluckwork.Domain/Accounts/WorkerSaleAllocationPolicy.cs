namespace Cluckwork.Domain.Accounts;

// #612 — how a restricted plain Worker's sale confirmation may draw stock.
// Only a plain Worker is affected; Owner, Manager, Sales, ReadOnly and
// unknown/Denied roles always confirm farm-wide regardless of this setting.
public enum WorkerSaleAllocationPolicy
{
    // Default for existing and new farms: a restricted Worker's confirmation
    // draws only from lots the worker is assigned to.
    AssignedFlocksOnly,

    // Explicit Owner/Manager opt-in: a restricted Worker's confirmation may
    // draw from any lot on the farm, same as every other role.
    AllFarmFlocks,
}
