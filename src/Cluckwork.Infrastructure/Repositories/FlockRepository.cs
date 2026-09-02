namespace Cluckwork.Infrastructure.Repositories;

using Cluckwork.Application.Features.Flocks;
using Cluckwork.Domain.Flocks;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

public sealed class FlockRepository(AppDbContext db) : IFlockRepository
{
    // Reads rely on the tenant query filter (AccountId == current tenant), so the
    // caller only ever sees its own flocks.
    // Tracked: DepleteFlockHandler mutates the returned entity.
    public Task<Flock?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.Flocks.FirstOrDefaultAsync(f => f.Id == id, ct);

    public Task<Flock?> GetByIdForFlockScopedWriteAsync(
        Guid id, Guid accountId, CancellationToken ct = default) =>
        db.Flocks
            .IgnoreQueryFilters()
            .Where(f => f.AccountId == accountId)
            .FirstOrDefaultAsync(f => f.Id == id, ct);

    // Read-only, paged. Archived flocks only surface in the management view.
    public async Task<IReadOnlyList<Flock>> ListAsync(
        int limit, int offset, bool includeArchived = false, CancellationToken ct = default) =>
        await db.Flocks
            .AsNoTracking()
            .Where(f => includeArchived || f.Status != FlockStatus.Archived)
            .OrderBy(f => f.Name).ThenBy(f => f.Id)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

    // #512 — discovery. Every predicate above is a WHERE clause: eligibility and
    // the literal search both run BEFORE the ORDER BY, so a page is never
    // shortened by filtering a window that was already cut (#512 evaluation
    // order). `ThenBy(f => f.Id)` is load-bearing, not decorative: duplicate
    // names are legal, and without the tie-break the same row can be served
    // twice across a page boundary or skipped entirely.
    public async Task<IReadOnlyList<Flock>> SearchAsync(
        string? search, FlockEligibility eligibility, int limit, int offset,
        CancellationToken ct = default)
    {
        var query = db.Flocks.AsNoTracking();

        query = eligibility switch
        {
            FlockEligibility.Active => query.Where(f => f.Status == FlockStatus.Active),
            FlockEligibility.ActiveAndDepleted =>
                query.Where(f => f.Status != FlockStatus.Archived),
            // All is the management view: the status predicate is skipped
            // rather than enumerated, so a future fourth FlockStatus cannot be
            // silently excluded by a list that aged out.
            _ => query,
        };

        var trimmed = LiteralSearch.Normalize(search);
        if (trimmed is not null)
        {
            var pattern = LiteralSearch.ContainsPattern(trimmed);
            query = query.Where(f => EF.Functions.ILike(f.Name, pattern, LiteralSearch.EscapeChar));
        }

        return await query
            .OrderBy(f => f.Name).ThenBy(f => f.Id)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);
    }

    // #512 US4 — scoped bulk display names for row projections (#512 T044).
    //
    // Ordinary LINQ over the filtered DbSet, so the model's structural
    // `AccountId AND flock-scope` filter composes here exactly as it does on the
    // list route (#613). That is not incidental: it is what makes an
    // `IgnoreQueryFilters()` mutation on this read observable, which is the proof
    // T053 requires. A FromSql/keyless source would compose NO filter, and
    // `IgnoreQueryFilters()` on a query that never had one is a no-op — the guard
    // would stay green against the very mutation it exists to catch.
    //
    // One query for the whole returned page, ids bounded to that page: a per-row
    // lookup is the N+1 the contract forbids. Archived and Depleted flocks
    // resolve because eligibility is deliberately NOT a predicate — a historical
    // row must name an Archived flock even though no picker offers it for new
    // selection.
    //
    // A missing key means outside-tenant, outside-scope or gone; callers fail
    // explicitly rather than substituting an id fragment (#512).
    public async Task<IReadOnlyDictionary<Guid, FlockReference>> GetDisplayNamesAsync(
        IReadOnlyCollection<Guid> flockIds, CancellationToken ct = default)
    {
        if (flockIds.Count == 0) return new Dictionary<Guid, FlockReference>();

        var ids = flockIds.Distinct().ToArray();
        var rows = await db.Flocks
            .AsNoTracking()
            .Where(f => ids.Contains(f.Id))
            .Select(f => new FlockReference(f.Id, f.Name, f.Status))
            // The tag is the query-count guard's only handle: it is what lets a
            // test count executions of THIS read rather than guess from generated
            // SQL. It must stay adjacent to the materialisation — see the comment
            // on ReferenceMarkers.
            .TagWith(ReferenceMarkers.FlockReference)
            .ToListAsync(ct);

        var map = new Dictionary<Guid, FlockReference>(rows.Count);
        foreach (var r in rows)
            map[r.Id] = r;
        return map;
    }

    public async Task AddAsync(Flock entity, CancellationToken ct = default) =>
        await db.Flocks.AddAsync(entity, ct);

    public void Update(Flock entity) => db.Flocks.Update(entity);

    public void Remove(Flock entity) => db.Flocks.Remove(entity);
}
