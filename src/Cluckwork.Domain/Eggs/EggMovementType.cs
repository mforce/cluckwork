namespace Cluckwork.Domain.Eggs;

// Spec §9.4 movement types. Production/Sale/Adjustment/Void come from the
// entry/sale lifecycle (#101); Discard/InternalUse/Reconciliation from the
// standalone stock write-off (#406). Transfer is reserved for a later phase.
[ModuleContract("EggOperations")]
public enum EggMovementType
{
    Production, Sale, Adjustment, Discard, InternalUse, Transfer, Reconciliation, Void,
}
