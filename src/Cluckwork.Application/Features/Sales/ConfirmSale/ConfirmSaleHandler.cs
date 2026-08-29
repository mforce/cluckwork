namespace Cluckwork.Application.Features.Sales.ConfirmSale;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Accounts;
using Cluckwork.Application.Features.EggGrades;
using Cluckwork.Application.Features.EggLots;
using Cluckwork.Application.Features.Eggs;
using Cluckwork.Application.Features.Users;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Eggs;
using Cluckwork.Domain.Sales;
using Microsoft.Extensions.Logging;

public sealed class ConfirmSaleHandler(
    IAccountRepository accounts,
    ISalesOrderRepository salesOrders,
    IEggLotRepository eggLots,
    IEggGradeRepository eggGrades,
    ISalesOrderAllocationRepository allocations,
    IEggInventoryMovementRepository eggMovements,
    IUserRoleAssignmentRepository assignments,
    IIdentityProvider identity,
    IAuditWriter audit,
    IUnitOfWork unitOfWork,
    IClock clock,
    ILogger<ConfirmSaleHandler> logger)
{
    // Only these roles may ever confirm a sale (SalesFlow's own route policy
    // already enforces this from the JWT — this is the FRESH re-check #612
    // asks for, since the caller's role can change while queued behind the
    // Account lock below). ReadOnly and Denied are refused with Auth.Forbidden.
    private static readonly EffectiveAccountRole[] AllowedToConfirm =
        [EffectiveAccountRole.Owner, EffectiveAccountRole.Manager,
         EffectiveAccountRole.Sales, EffectiveAccountRole.Worker];

    // The generic farm-wide-shortfall message for a restricted Worker: reused
    // by both the AssignedFlocksOnly farm-wide retry AND the AllFarmFlocks
    // path (#612 privacy fix) — a restricted Worker must never see the
    // grade/quantity detail in EggLot.InsufficientStock's other branch.
    private static Error GenericInsufficientStock() =>
        Error.Domain(
            "EggLot.InsufficientStock",
            "There is not enough stock available to confirm this sale.");

    // Implements functional spec §10.9.1 pessimistic-lock FIFO allocation,
    // extended by #612's Account → SalesOrder → EggLots lock ordering:
    //   BEGIN
    //     SELECT Account FOR SHARE (source of the farm date AND the policy)
    //     SELECT SalesOrder FOR UPDATE (fresh — never a pre-transaction read)
    //     CheckCanConfirm() before touching stock
    //     re-read the caller's effective role; a now-forbidden caller -> 403
    //     for a plain Worker, read committed UserRoleAssignment rows
    //     SELECT candidate egg_lots FOR UPDATE (ONE statement, farm-wide)
    //     plan the whole order in memory (assigned-first under AssignedFlocksOnly)
    //     apply only a successful plan; update egg_lots.quantity_available + version
    //   COMMIT  (conflict/insufficient → 409/422)
    public async Task<Result<ConfirmSaleResponse>> HandleAsync(
        ConfirmSaleCommand command, Guid accountId, Guid actingUserId, CancellationToken ct)
    {
        Result<ConfirmSaleResponse>? failure = null;
        SalesOrder? confirmedOrder = null;
        var allocationDate = default(DateOnly);

        await unitOfWork.ExecuteInTransactionAsync(async transactionCt =>
        {
            // 1 — Account FOR SHARE: also the single source of the farm date
            // and the allocation policy read below, so both see the SAME row
            // a concurrent settings/currency write would otherwise race.
            var account = await accounts.GetCurrentSharedLockedAsync(transactionCt);
            if (account is null)
            {
                failure = Result.Failure<ConfirmSaleResponse>(Error.NotFound(nameof(Account), accountId));
                return false;
            }

            // 2 — the farm date from the JUST-locked row, never a separately
            // (and possibly staler) read Account instance. Same fail-closed
            // shape as FarmClock: an unusable zone must throw, not guess UTC.
            try
            {
                allocationDate = clock.TodayInZone(account.TimeZoneId);
            }
            catch (Exception ex) when (
                ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
            {
                // Same fail-closed contract as FarmClock (Application cannot
                // reference its Infrastructure exception type, so this is a
                // plain invariant failure): an unusable stored zone means
                // something is wired wrong, not "this farm is on UTC".
                throw new InvalidOperationException(
                    $"The farm's timezone ('{account.TimeZoneId}') is not usable, " +
                    "so no date-dependent operation can be trusted.", ex);
            }

            // 3 — SalesOrder FOR UPDATE, fresh. Serializes a confirm racing a
            // confirm/void of the SAME order; the old pre-transaction tracked
            // read is gone.
            var order = await salesOrders.GetByIdLockedAsync(accountId, command.SalesOrderId, transactionCt);
            if (order is null)
            {
                failure = Result.Failure<ConfirmSaleResponse>(
                    Error.NotFound(nameof(SalesOrder), command.SalesOrderId));
                return false;
            }
            if (order.AccountId != accountId)
            {
                failure = Result.Failure<ConfirmSaleResponse>(AppError.TenantMismatch());
                return false;
            }

            // 4 — the precondition BEFORE touching stock: a NotDraft/NoItems
            // order must never even attempt a FIFO lock.
            var canConfirm = order.CheckCanConfirm();
            if (canConfirm.IsFailure)
            {
                failure = Result.Failure<ConfirmSaleResponse>(canConfirm.Error);
                return false;
            }

            // 5 — a FRESH read, not the JWT-issued caller's own claims: the
            // caller's role can have changed while queued behind the Account
            // lock above.
            var role = await identity.GetEffectiveRoleAsync(accountId, actingUserId, transactionCt);
            if (role is null || !AllowedToConfirm.Contains(role.Value))
            {
                failure = Result.Failure<ConfirmSaleResponse>(AppError.Forbidden());
                return false;
            }

            // 6 — for a plain Worker ONLY, read committed assignments right
            // before stock (no lock — "committed" is the contract; assignment
            // add/remove is not newly serialized). Zero rows or any farm-wide
            // row (FlockId null) is Unrestricted, matching FlockScopeGuard.
            HashSet<Guid>? assignedFlockIds = null;
            if (role == EffectiveAccountRole.Worker)
            {
                var rows = await assignments.ListByUserAsync(actingUserId, transactionCt);
                if (rows.Count > 0 && rows.All(r => r.FlockId is not null))
                    assignedFlockIds = rows.Select(r => r.FlockId!.Value).ToHashSet();
            }
            var isRestrictedWorker = assignedFlockIds is not null;

            // 7 — ONE farm-wide FIFO lock statement for every grade on the
            // order, ordered (ProductionDate, Id). Never filtered by flock in
            // SQL and never called twice (#612 — EggLotRepository keeps this
            // shape on purpose).
            var gradeIds = order.Items.Select(i => i.EggGradeId).Distinct().ToList();
            var lockedLots = await eggLots.GetAvailableFifoLockedAsync(
                accountId, gradeIds, allocationDate, transactionCt);

            // 8/9 — plan in memory, never mutating a lot or the order.
            var useAssignedFirst = isRestrictedWorker
                && account.WorkerSaleAllocationPolicy == WorkerSaleAllocationPolicy.AssignedFlocksOnly;

            SaleAllocationPlan plan;
            if (useAssignedFirst)
            {
                var assignedLots = lockedLots.Where(l => assignedFlockIds!.Contains(l.FlockId)).ToList();
                var assignedPlan = SaleAllocationPlanner.Plan(order.Items, assignedLots);
                if (assignedPlan.IsComplete)
                {
                    plan = assignedPlan;
                }
                else
                {
                    // Retry against the SAME already-locked farm-wide rows —
                    // no second query, no new lock.
                    var farmWidePlan = SaleAllocationPlanner.Plan(order.Items, lockedLots);
                    if (farmWidePlan.IsComplete)
                    {
                        // Farm-wide would have covered it, but a restricted
                        // Worker under AssignedFlocksOnly may not draw outside
                        // their own flocks — the distinct code, a generic
                        // description (no grade/quantity/flock facts).
                        failure = Result.Failure<ConfirmSaleResponse>(Error.Domain(
                            "EggLot.AssignedFlocksInsufficientStock",
                            "Your assigned flocks do not have enough stock for this sale. " +
                            "An owner or manager can enable selling from other flocks in Farm settings."));
                        return false;
                    }

                    // Farm-wide ALSO fails: the existing code, but a generic
                    // description for a restricted Worker — it must not leak
                    // farm-wide stock levels a Worker cannot see.
                    failure = Result.Failure<ConfirmSaleResponse>(GenericInsufficientStock());
                    return false;
                }
            }
            else
            {
                plan = SaleAllocationPlanner.Plan(order.Items, lockedLots);
                if (!plan.IsComplete)
                {
                    if (isRestrictedWorker)
                    {
                        // A restricted Worker reaches farm-wide planning under
                        // AllFarmFlocks too (#612 privacy fix) — this policy
                        // lets their confirmation draw from any flock, but it
                        // must still never reveal farm-wide grade/quantity
                        // detail to a Worker who cannot see that stock.
                        failure = Result.Failure<ConfirmSaleResponse>(GenericInsufficientStock());
                        return false;
                    }

                    // Unchanged behavior for every non-restricted caller
                    // (elevated roles, farm-wide policy, or an unrestricted
                    // Worker): today's specific grade/quantity message.
                    var gradeName = (await eggGrades.GetByIdAsync(plan.ShortEggGradeId!.Value, transactionCt))?.Name
                        ?? plan.ShortEggGradeId.Value.ToString();
                    failure = Result.Failure<ConfirmSaleResponse>(Error.Domain(
                        "EggLot.InsufficientStock",
                        $"Insufficient stock for grade '{gradeName}': {plan.ShortRemaining} eggs unallocated."));
                    return false;
                }
            }

            // 10 — apply ONLY the successful plan. Every lot in a draw is one
            // of the SAME locked instances the plan was computed from, so
            // Allocate() re-validating quantity here cannot disagree with the
            // plan — a contradiction is an invariant violation, not a normal
            // domain failure, and must not be swallowed into a 422.
            var lotsById = lockedLots.ToDictionary(l => l.Id);
            var allocationRows = new List<SalesOrderAllocation>();
            foreach (var draw in plan.Draws)
            {
                var lot = lotsById[draw.EggLotId];
                var allocateResult = lot.Allocate(draw.Quantity, allocationDate);
                if (allocateResult.IsFailure)
                    throw new InvalidOperationException(
                        $"Sale allocation plan contradicted EggLot.Allocate for lot {lot.Id}: " +
                        allocateResult.Error.Description);

                var allocation = SalesOrderAllocation.Create(
                    accountId, order.Id, draw.SalesOrderItemId, draw.EggLotId, draw.Quantity);
                allocationRows.Add(allocation);
                // Ledger row (#101): the draw leaves the lot as an explicit
                // Sale movement, same transaction. References the ALLOCATION,
                // not the order — two same-grade lines drawing from one lot
                // stay distinguishable (codex #102).
                await eggMovements.AddAsync(EggInventoryMovement.Create(
                    Guid.NewGuid(), accountId, lot.Id, EggMovementType.Sale,
                    -draw.Quantity, nameof(SalesOrderAllocation), allocation.Id, clock.UtcNow), transactionCt);
            }

            var confirmResult = order.Confirm();
            if (confirmResult.IsFailure)
                throw new InvalidOperationException(
                    $"SalesOrder.Confirm contradicted its own CheckCanConfirm for order {order.Id}: " +
                    confirmResult.Error.Description);

            await allocations.AddRangeAsync(allocationRows, transactionCt);

            // #494 — INSIDE the transaction, so the event commits with the
            // FIFO allocations or rolls back with them.
            await audit.WriteAsync(
                AuditActions.SalesOrderConfirm, nameof(SalesOrder), order.Id, ct: transactionCt);

            confirmedOrder = order;
            return true;
        }, ct);

        if (failure is not null)
            return failure.LogFailure(logger, "ConfirmSale");

        logger.LogInformation(
            "Sales order {SalesOrderId} confirmed on {AllocationDate}: {ItemCount} lines allocated FIFO",
            confirmedOrder!.Id, allocationDate, confirmedOrder.Items.Count);
        return Result.Success(new ConfirmSaleResponse(confirmedOrder.Id, confirmedOrder.Status.ToString()));
    }
}
