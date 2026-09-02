namespace Cluckwork.Application.Features.Flocks;

using Cluckwork.Domain.Flocks;

// Current display data for one flock, read out of the tenant's own visible
// flocks so a returned page of rows can name its flocks in one grouped lookup
// instead of leaning on whatever picker results the SPA happens to have loaded
// (#512). Names and status are *current*, not a historical snapshot.
//
// Never stored on the referring aggregate: Daily Entry, Feed Usage, Water
// Usage, User assignment and Expense responses project it; they do not own it.
public sealed record FlockReference(Guid Id, string Name, FlockStatus Status);
