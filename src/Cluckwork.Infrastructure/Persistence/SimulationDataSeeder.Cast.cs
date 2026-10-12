using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Domain.Modules.Farm.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cluckwork.Infrastructure.Persistence;

public sealed partial class SimulationDataSeeder
{
    // --- Cast: Managers, Sales, ReadOnly, Workers (role-less) ---------

    // #500 — one seeded persona, carrying everything the acting context needs.
    //
    // Roles is NOT a label. FlockScopeGuard reads it (via CurrentUserContext,
    // which stores what ActAs passes it verbatim) and lets Owner/Manager through
    // by role alone, so a SimActor built with the wrong roles changes what the
    // fixture is ALLOWED to write, not merely whose name lands on the audit row.
    private sealed record SimActor(Guid UserId, string Email, IReadOnlyList<string> Roles);

    // The personas the fixture acts as. ReadOnly is deliberately absent: those
    // users are created (the load test signs in as them) but no phase ever picks
    // one, which is what makes "a ReadOnly persona authors nothing" a property of
    // this type rather than of a rule someone has to remember.
    //
    // The restricted pair is Guid.Empty until RestrictOneWorkerAsync reports it,
    // which is why every phase that needs it runs after that call.
    private sealed record SimCast(
        SimActor Owner,
        IReadOnlyList<SimActor> Managers,
        IReadOnlyList<SimActor> Sales,
        IReadOnlyList<SimActor> Workers,
        Guid RestrictedWorkerId,
        Guid RestrictedFlockId);

    // Rotates deterministically through a pool. TOTAL for every pool, including
    // an empty one: SimulationOptions validates no count, so Managers/Sales/
    // Workers may each be 0 and `pool[index % pool.Count]` would divide by zero.
    //
    // The fallback is a fidelity DEGRADATION, so it is never silent (#500 review
    // round 2, finding 6) — hiding it would be the same class of defect #500
    // reports. Failing instead would be worse: Workers == 0 is already a
    // deliberately tolerated configuration (RestrictOneWorkerAsync warns and
    // carries on).
    private SimActor Pick(IReadOnlyList<SimActor> pool, int index, SimActor fallback, string poolName) =>
        Pick(pool, index, () => fallback, poolName);

    // Deferred-fallback overload, and the reason it exists: WorkerFor nests one
    // Pick inside another, and C# evaluates the inner call eagerly as an
    // ARGUMENT. Through the overload above, that ran the manager-pool Pick on
    // every production day and — at Managers == 0 — logged "provenance is
    // degraded" for each of them, even though a worker was available and
    // nothing had been substituted. A warning that fires when the thing it
    // warns about did not happen is worse than no warning. (#500 mid-point
    // review.)
    private SimActor Pick(IReadOnlyList<SimActor> pool, int index, Func<SimActor> fallback, string poolName)
    {
        if (pool.Count > 0) return pool[index % pool.Count];

        var substitute = fallback();
        logger.LogWarning(
            "Simulation cast pool {Pool} is empty — rows a {Pool} persona would have authored are " +
            "attributed to {Fallback} instead. The seeded fixture's provenance is degraded.",
            poolName, poolName, substitute.Email);
        return substitute;
    }

    // Declares who the following handler calls act as. A pure assignment:
    // CurrentUserContext.Resolve stores all three values verbatim — no database
    // read, no role re-fetch — so the roles passed here are exactly the ones
    // FlockScopeGuard reads.
    private void ActAs(SimActor actor) => currentUser.Resolve(actor.UserId, actor.Email, actor.Roles);

    private async Task<SimCast> SeedCastAsync(
        Guid accountId, SimActor owner, SimulationOptions sim, CancellationToken ct)
    {
        var managers = new List<SimActor>();
        for (var i = 1; i <= sim.Managers; i++)
            managers.Add(await EnsureUserAsync(
                accountId, $"sim-manager-{i}@{sim.EmailDomain}", Roles.Manager,
                $"Sim Manager {i}", sim.CastPassword, ct));

        var sales = new List<SimActor>();
        for (var i = 1; i <= sim.Sales; i++)
            sales.Add(await EnsureUserAsync(
                accountId, $"sim-sales-{i}@{sim.EmailDomain}", Roles.Sales,
                $"Sim Sales {i}", sim.CastPassword, ct));

        // Created, never picked — see SimCast. The load test signs in as these
        // to exercise read-only routes; the fixture itself never acts as one.
        for (var i = 1; i <= sim.ReadOnly; i++)
            await EnsureUserAsync(
                accountId, $"sim-readonly-{i}@{sim.EmailDomain}", Roles.ReadOnly,
                $"Sim ReadOnly {i}", sim.CastPassword, ct);

        // Workers deliberately carry no role row (Roles.cs) — the seeder's own
        // storedRole conversion below maps WorkerRole to a null role.
        var workers = new List<SimActor>();
        for (var i = 1; i <= sim.Workers; i++)
            workers.Add(await EnsureUserAsync(
                accountId, $"sim-worker-{i}@{sim.EmailDomain}", WorkerRole,
                $"Sim Worker {i}", sim.CastPassword, ct));

        return new SimCast(owner, managers, sales, workers, Guid.Empty, Guid.Empty);
    }

    // Which worker records a given flock's eggs on a given day.
    //
    // This is an AUTHORIZATION rule, not a fidelity nicety. RecordDailyEntry and
    // SubmitDailyEntry both run FlockScopeGuard, which reads the very context
    // ActAs writes; the one worker RestrictOneWorkerAsync narrowed carries a
    // UserRoleAssignment row, so picking it for any other flock returns
    // FlockScope.NotAssigned and fails the entire seed. Workers with no
    // assignment row stay account-wide (the guard grandfathers them).
    //
    // Eligibility is therefore per-flock, and the counts differ: at the default
    // cast the restricted flock has all 3 workers eligible and every other flock
    // has 2.
    private SimActor WorkerFor(SimCast cast, Guid flockId, int dayIndex)
    {
        var eligible = flockId == cast.RestrictedFlockId
            ? cast.Workers
            // Materialized because Pick takes an IReadOnlyList.
            : [.. cast.Workers.Where(w => w.UserId != cast.RestrictedWorkerId)];

        // Total by construction: an empty worker pool falls back to a manager,
        // and an empty manager pool falls back to the Owner, who always exists
        // (MissingBaseDataAsync refuses to seed without one). Both fallbacks
        // warn — and the manager one is DEFERRED, so it warns only when the
        // worker pool was actually empty. See the Pick overloads.
        return Pick(
            eligible, dayIndex,
            () => Pick(cast.Managers, dayIndex, cast.Owner, "Managers"),
            "Workers");
    }

    // #500 — returns the persona, not just its id.
    //
    // Both branches read actual Roles through Access, rather
    // than the create branch trusting the role name it just passed in. That is
    // deliberate: a persona rebuilt differently on a re-run than on a first run
    // is exactly the silent divergence this fixture's determinism contract
    // (#279) exists to prevent, and Roles is an authorization input.
    private async Task<SimActor> EnsureUserAsync(
        Guid accountId, string email, string role, string name, string password, CancellationToken ct)
    {
        // #532 — ACCOUNT-SCOPED, and this is the one that mattered. The global
        // lookup adopted ANOTHER farm's user as this account's persona when only
        // a foreign row existed, and threw outright once both farms had one
        // (UserStore backs FindByEmailAsync with SingleOrDefaultAsync).
        // SimActor.Roles is an authorization input (#500), so a mis-attributed
        // persona is a tenant-isolation defect, not a cosmetic one.
        var existing = await access.FindUserByEmailAsync(accountId, email, ct);
        if (existing is null)
        {
            // #360 — the simulation command is a trusted, non-HTTP one-shot
            // caller. Interactive creation is fail-closed in CreateUserHandler;
            // this seeder uses Access fixture creation explicitly while
            // acting as the real Owner resolved by SeedAsync. There is no flag,
            // magic actor id, environment branch, or request-selectable bypass.
            var storedRole = role == WorkerRole ? null : role;
            var result = await operations.CreateUserAsync(
                accountId, email.Trim(), password, storedRole,
                name: UserName.Normalize(name), ct: ct);
            Require(result, $"create cast user {email}");

            existing = await access.FindUserByEmailAsync(accountId, email, ct)
                ?? throw new InvalidOperationException(
                    $"Simulation seed: cast user {email} was created but cannot be read back.");
        }

        var roles = existing.Roles;

        // #500 (codex round 4) — a DISABLED cast member is refused, for exactly
        // the reason FindOwnerAsync refuses a disabled Owner one screen up.
        //
        // This only bites on a PARTIAL RE-RUN: the persona was created by an
        // earlier run, somebody disabled it through the Users screen, and this
        // run finds it by email. Disabling stamps DisabledAt and keeps every
        // role row, so FindByEmailAsync returns it and the role check below
        // passes — the seeder would then attribute newly written fixture history
        // to a persona LoginAsync rejects (IdentityProvider line 41). The seed
        // reports success and the fixture is unusable in the one way nobody
        // checks, because the rows look perfectly well-formed.
        if (existing.DisabledAt is not null)
            throw new InvalidOperationException(
                $"Simulation seed: cast user {email} exists but is DISABLED, so the fixture would name an " +
                "actor who cannot log in. Re-enable that user from the Users screen (which clears both " +
                "DisabledAt and DisabledBy), or reset the fixture.");

        // The persona must still HOLD the role this call site is staffing, or
        // the fixture is not what it says it is (#500 final review, raised
        // independently by two reviewers).
        //
        // Fetching the roles rather than fabricating them is right, but it means
        // an existing user whose role was changed out from under the seeder is
        // silently accepted into the wrong pool. A `sim-manager-1` demoted to
        // ReadOnly would go on authoring the manager phases — and, holding no
        // UserRoleAssignment rows, would sail through FlockScopeGuard on its
        // "zero rows = account-wide" branch rather than being refused. The seed
        // would then fail its exact-count validation much later, AFTER durable
        // writes, with a message pointing at counts instead of at the cause.
        //
        // Fail here instead, naming both roles.
        //
        // #500 (codex round 4) — the Worker case is checked, not EXEMPT. "Worker"
        // is a pseudo-role the seeder's storedRole conversion maps to null, so a worker holds no
        // assignable role — which means the correct assertion for a worker is
        // `roles is empty`, not "skip the check". Exempting it let a sim-worker
        // promoted to Manager through the Users UI pass as a worker, and that is
        // the worst version of this bug rather than a cosmetic one: the whole
        // restricted-worker shape this fixture exists to exercise is a
        // FlockScopeGuard narrowing, and Manager BYPASSES that guard by role. The
        // rerun would go green having tested the opposite of what it claims.
        if (role == WorkerRole)
        {
            if (roles.Count > 0)
                throw new InvalidOperationException(
                    $"Simulation seed: cast user {email} is the fixture's WORKER persona, which must hold no " +
                    $"assignable role, but it holds: {string.Join(", ", roles)}. A worker promoted outside the " +
                    "seeder no longer exercises the FlockScopeGuard narrowing this fixture depends on " +
                    "(Manager and Owner bypass it by role). Restore that user to Worker, or reset the fixture.");
        }
        else if (!roles.Contains(role))
            throw new InvalidOperationException(
                $"Simulation seed: cast user {email} exists but does not hold the {role} role " +
                $"(it holds: {(roles.Count == 0 ? "no roles" : string.Join(", ", roles))}). The cast was " +
                "reconfigured outside the seeder; fix that user's role, or reset the fixture.");

        return new SimActor(existing.Id, existing.Email!, roles);
    }
}
