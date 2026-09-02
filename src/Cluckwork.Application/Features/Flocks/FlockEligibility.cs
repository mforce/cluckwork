namespace Cluckwork.Application.Features.Flocks;

// Which flock statuses a read may return (#512). A policy for a *read*, never a
// change to the FlockStatus lifecycle itself. The endpoint layer parses the wire
// values (`active`, `active-and-depleted`, `all`) case-sensitively and rejects
// anything else, so this enum carries no parsing of its own.
//
// The default is ActiveAndDepleted: that is what every caller that says nothing
// gets today, and the compatibility rule is that it keeps getting exactly that.
public enum FlockEligibility
{
    // Active only — new worker assignments.
    Active,

    // Active and Depleted — omitted eligibility, and Daily Entry / Feed / Water capture.
    ActiveAndDepleted,

    // Active, Depleted and Archived — History, the Feed/Water filters, Expenses.
    All,
}
