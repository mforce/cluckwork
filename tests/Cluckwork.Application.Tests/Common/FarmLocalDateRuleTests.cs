namespace Cluckwork.Application.Tests.Common;

using Cluckwork.Application.Features.Expenses.AdjustExpense;
using Cluckwork.Application.Features.Expenses.CreateExpense;
using Cluckwork.Application.Features.Flocks.UpdateFlock;
using Cluckwork.Application.Features.Sales.RecordPayment;

// #155 — the four validators the sweep moved onto the farm clock that had no
// unit tests of their own. Each one gets the same pair: the farm's today is
// accepted, the day after it is not. The dates are measured against a fixed
// FixedFarmClock rather than the build machine, which is the point — under the
// old rule these compared against UTC, so for a farm ahead of UTC its own today
// was refused as being "in the future".
public sealed class FarmLocalDateRuleTests
{
    private static readonly DateOnly FarmToday = FixedFarmClock.Today;
    private static readonly DateOnly FarmTomorrow = FarmToday.AddDays(1);

    // --- Payment ---------------------------------------------------------
    private static RecordPaymentCommand Payment(DateOnly date) => new(
        SalesOrderId: Guid.NewGuid(), PaymentDate: date, AmountMinorUnits: 5_00,
        Method: "Cash", ReferenceNumber: null, Note: null);

    [Fact]
    public async Task PaymentDated_FarmToday_Passes()
    {
        var v = new RecordPaymentValidator(FixedFarmClock.AtDefault());
        Assert.True((await v.ValidateAsync(Payment(FarmToday))).IsValid);
    }

    [Fact]
    public async Task PaymentDated_AfterFarmToday_Fails()
    {
        var v = new RecordPaymentValidator(FixedFarmClock.AtDefault());
        var result = await v.ValidateAsync(Payment(FarmTomorrow));
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(RecordPaymentCommand.PaymentDate));
    }

    // --- Expense (create) ------------------------------------------------
    private static CreateExpenseCommand Expense(DateOnly date) => new(
        ExpenseCategoryId: Guid.NewGuid(), Date: date, Description: "Feed delivery",
        AmountMinorUnits: 12_00, FlockId: null, Note: null);

    [Fact]
    public async Task ExpenseDated_FarmToday_Passes()
    {
        var v = new CreateExpenseValidator(FixedFarmClock.AtDefault());
        Assert.True((await v.ValidateAsync(Expense(FarmToday))).IsValid);
    }

    [Fact]
    public async Task ExpenseDated_AfterFarmToday_Fails()
    {
        var v = new CreateExpenseValidator(FixedFarmClock.AtDefault());
        var result = await v.ValidateAsync(Expense(FarmTomorrow));
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(CreateExpenseCommand.Date));
    }

    // --- Expense (adjust) ------------------------------------------------
    private static AdjustExpenseCommand ExpenseAdjust(DateOnly date) => new(
        ExpenseId: Guid.NewGuid(), Version: 1, ExpenseCategoryId: Guid.NewGuid(), Date: date,
        Description: "Feed delivery", AmountMinorUnits: 12_00, FlockId: null, Note: null);

    [Fact]
    public async Task ExpenseAdjustDated_FarmToday_Passes()
    {
        var v = new AdjustExpenseValidator(FixedFarmClock.AtDefault());
        Assert.True((await v.ValidateAsync(ExpenseAdjust(FarmToday))).IsValid);
    }

    [Fact]
    public async Task ExpenseAdjustDated_AfterFarmToday_Fails()
    {
        var v = new AdjustExpenseValidator(FixedFarmClock.AtDefault());
        var result = await v.ValidateAsync(ExpenseAdjust(FarmTomorrow));
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(AdjustExpenseCommand.Date));
    }

    // --- Flock update (placement date) -----------------------------------
    private static UpdateFlockCommand FlockUpdate(DateOnly placedOn) => new(
        FlockId: Guid.NewGuid(), Name: "House 1 layers", Breed: "ISA Brown",
        PlacementDate: placedOn, InitialCount: 500);

    [Fact]
    public async Task FlockUpdatePlacedOn_FarmToday_Passes()
    {
        var v = new UpdateFlockValidator(FixedFarmClock.AtDefault());
        Assert.True((await v.ValidateAsync(FlockUpdate(FarmToday))).IsValid);
    }

    [Fact]
    public async Task FlockUpdatePlacedOn_AfterFarmToday_Fails()
    {
        var v = new UpdateFlockValidator(FixedFarmClock.AtDefault());
        var result = await v.ValidateAsync(FlockUpdate(FarmTomorrow));
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(UpdateFlockCommand.PlacementDate));
    }
}
