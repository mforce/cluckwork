namespace Cluckwork.Application.Tests.Sales;

using Cluckwork.Application.Features.Sales.ConfirmSale;
using Cluckwork.Domain.Catalog;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Eggs;
using Cluckwork.Domain.Sales;

// #612 — the pure whole-order FIFO planner. Never mutates a lot or the order;
// ConfirmSaleHandler decides which candidate lot list to plan against and
// applies the winning plan for real afterward.
public sealed class SaleAllocationPlannerTests
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);
    private static readonly Guid GradeA = Guid.NewGuid();
    private static readonly Guid GradeB = Guid.NewGuid();

    private static SalesOrder OrderWith(params (Guid GradeId, int Quantity)[] lines)
    {
        var order = SalesOrder.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "SO-PLAN", Today, "USD");
        foreach (var (gradeId, quantity) in lines)
            order.AddItem(Guid.NewGuid(), ProductType.Egg, gradeId, ProductUnit.Egg, 1, quantity, Money.Zero("USD"));
        return order;
    }

    private static EggLot Lot(Guid gradeId, int quantity, DateOnly? productionDate = null, Guid? flockId = null) =>
        EggLot.Create(
            Guid.NewGuid(), Guid.NewGuid(), flockId ?? Guid.NewGuid(),
            productionDate ?? Today, gradeId, quantity);

    [Fact]
    public void WholeOrderFits_ProducesOneDrawPerLot_InFifoOrder()
    {
        var older = Lot(GradeA, 10, Today.AddDays(-2));
        var newer = Lot(GradeA, 10, Today.AddDays(-1));
        var order = OrderWith((GradeA, 12));

        var plan = SaleAllocationPlanner.Plan(order.Items, [older, newer]);

        Assert.True(plan.IsComplete);
        Assert.Equal(2, plan.Draws.Count);
        Assert.Equal(older.Id, plan.Draws[0].EggLotId);
        Assert.Equal(10, plan.Draws[0].Quantity);
        Assert.Equal(newer.Id, plan.Draws[1].EggLotId);
        Assert.Equal(2, plan.Draws[1].Quantity);
    }

    [Fact]
    public void RepeatedGrade_AcrossTwoLines_SharesTheSameLotWithoutDoubleCounting()
    {
        var lot = Lot(GradeA, 15);
        var order = OrderWith((GradeA, 10), (GradeA, 5));

        var plan = SaleAllocationPlanner.Plan(order.Items, [lot]);

        Assert.True(plan.IsComplete);
        Assert.Equal(2, plan.Draws.Count);
        Assert.Equal(10, plan.Draws[0].Quantity);
        Assert.Equal(5, plan.Draws[1].Quantity);
    }

    [Fact]
    public void DifferentGrades_DrawFromTheirOwnLotsOnly()
    {
        var lotA = Lot(GradeA, 10);
        var lotB = Lot(GradeB, 10);
        var order = OrderWith((GradeA, 6), (GradeB, 4));

        var plan = SaleAllocationPlanner.Plan(order.Items, [lotA, lotB]);

        Assert.True(plan.IsComplete);
        Assert.Contains(plan.Draws, d => d.EggLotId == lotA.Id && d.Quantity == 6);
        Assert.Contains(plan.Draws, d => d.EggLotId == lotB.Id && d.Quantity == 4);
    }

    [Fact]
    public void InsufficientStock_ReportsTheShortGradeAndRemainder_AndProducesNoDraws()
    {
        var lot = Lot(GradeA, 3);
        var order = OrderWith((GradeA, 10));

        var plan = SaleAllocationPlanner.Plan(order.Items, [lot]);

        Assert.False(plan.IsComplete);
        Assert.Equal(GradeA, plan.ShortEggGradeId);
        Assert.Equal(7, plan.ShortRemaining);
        Assert.Empty(plan.Draws);
    }

    [Fact]
    public void CandidateLots_AreNeverMutated()
    {
        var lot = Lot(GradeA, 10);
        var before = lot.QuantityAvailable;
        var order = OrderWith((GradeA, 6));

        SaleAllocationPlanner.Plan(order.Items, [lot]);

        Assert.Equal(before, lot.QuantityAvailable);
    }

    [Fact]
    public void SameInputs_PlanTwice_ProducesTheIdenticalPlan()
    {
        // The caller relies on this: an assigned-only subset failing and then
        // replanning the SAME full locked list must not depend on any state
        // left over from the first attempt.
        var lot = Lot(GradeA, 10);
        var order = OrderWith((GradeA, 6));

        var first = SaleAllocationPlanner.Plan(order.Items, [lot]);
        var second = SaleAllocationPlanner.Plan(order.Items, [lot]);

        Assert.Equal(first.Draws, second.Draws);
    }

    [Fact]
    public void EmptyCandidateList_IsInsufficient_ForAnyNonZeroLine()
    {
        var order = OrderWith((GradeA, 1));
        var plan = SaleAllocationPlanner.Plan(order.Items, []);
        Assert.False(plan.IsComplete);
        Assert.Equal(GradeA, plan.ShortEggGradeId);
        Assert.Equal(1, plan.ShortRemaining);
    }
}
