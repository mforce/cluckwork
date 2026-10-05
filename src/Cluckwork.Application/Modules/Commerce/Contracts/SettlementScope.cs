namespace Cluckwork.Application.Modules.Commerce.Contracts;

// #769 — how much of the settlement figure this read may touch. An enum rather
// than a `bool includeOutstanding` + `bool unpaidOnly` pair because that pair
// makes "hide the money but filter by it" representable, and answering a
// question the caller may not see the answer to is the defect this issue
// exists to end.
[ModuleContract("Commerce")]
public enum SettlementScope
{
    // No settlement figure and no settlement predicate. The query must not
    // name Payments at all — a structural gate, not a null applied afterwards.
    Hidden,

    // Every row carries its outstanding figure; no settlement predicate.
    Visible,

    // Every row carries its outstanding figure AND the page is restricted to
    // orders still owing money.
    UnpaidOnly,
}
