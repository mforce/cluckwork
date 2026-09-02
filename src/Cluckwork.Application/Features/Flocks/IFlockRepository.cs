namespace Cluckwork.Application.Features.Flocks;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Flocks;

public interface IFlockRepository : IRepository<Flock, Guid>
{
    // Archived flocks are hidden by default — they only appear in the
    // management view (includeArchived: true). Depleted flocks stay visible.
    Task<IReadOnlyList<Flock>> ListAsync(
        int limit, int offset, bool includeArchived = false, CancellationToken ct = default);

    // #512 — picker discovery. The WHERE clauses are all applied before
    // OrderBy/Skip/Take, so a page never shortens because a row was dropped
    // after the window was cut. `search` is matched literally (case-
    // insensitive substring, `%`/`_`/`\` as data); blank or whitespace-only
    // search is unfiltered. The tenant AND flock-scope global filter stays
    // structural here — no IgnoreQueryFilters (#613).
    Task<IReadOnlyList<Flock>> SearchAsync(
        string? search, FlockEligibility eligibility, int limit, int offset,
        CancellationToken ct = default);

    // #512 US4 — scoped bulk display names for row projections (T044): one read
    // resolves the distinct flock ids of a whole returned page, so a historical
    // row can show its flock's CURRENT name and status independently of any
    // picker page. Archived and Depleted flocks resolve — eligibility is not a
    // predicate here, because a row must stay readable for a flock no picker will
    // offer for NEW selection.
    //
    // A missing key means the flock is outside this tenant, outside this Worker's
    // flock scope, or gone; it is NOT "unnamed". Callers must fail explicitly
    // rather than substitute an id fragment — the defect this story closes.
    //
    // IMPLEMENTED AS ORDINARY FILTERED LINQ over the model's Flocks set, so the
    // structural `AccountId AND flock-scope` filter composes here exactly as it does
    // on the list route (#613) — it is NOT raw SQL and there is no hand-written
    // tenant predicate. That is what keeps a scope bypass testable: `IgnoreQueryFilters()`
    // is a real mutation here, and the tenant and Worker guards in
    // NamedRowProjectionTests redden on it. Never reach for it, or for FromSql, to
    // "fix" a name that fails to resolve — an unresolvable name is the scope filter
    // answering, and a FromSql source would compose NO filter at all, leaving the
    // mutation unobservable.
    Task<IReadOnlyDictionary<Guid, FlockReference>> GetDisplayNamesAsync(
        IReadOnlyCollection<Guid> flockIds, CancellationToken ct = default);

    // Write-side lifecycle lookup (#388). Bypasses the request-start flock
    // snapshot after the live FlockScopeGuard succeeds, but reinstates AccountId
    // explicitly. This closes the assignment-change race without exposing flock
    // state to an unassigned caller or crossing tenants.
    Task<Flock?> GetByIdForFlockScopedWriteAsync(
        Guid id, Guid accountId, CancellationToken ct = default);
}
