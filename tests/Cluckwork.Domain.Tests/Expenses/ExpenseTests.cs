namespace Cluckwork.Domain.Tests.Expenses;

using Cluckwork.Domain.Expenses;

public sealed class ExpenseTests
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    private static Expense Make(long amount = 1500) =>
        Expense.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Today, "Feed delivery", amount, "USD", 2);

    [Fact]
    public void Create_TrimsAndDefaults()
    {
        var e = Expense.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Today, "  Feed delivery  ", 1500, "USD", 2, note: "   ");
        Assert.Equal("Feed delivery", e.Description);
        Assert.Null(e.Note);
        Assert.Null(e.FlockId);
        Assert.Equal(0, e.Version);
        Assert.Equal("USD", e.CurrencyCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Create_NonPositiveAmount_Throws(long amount)
    {
        Assert.Throws<ArgumentException>(() => Make(amount));
    }

    [Fact]
    public void Create_BlankDescription_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            Expense.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                Today, "   ", 100, "USD", 2));
    }

    [Fact]
    public void Adjust_ReplacesFields_BumpsVersion_KeepsCurrency()
    {
        var e = Make();
        var newCategory = Guid.NewGuid();
        var flock = Guid.NewGuid();

        var result = e.Adjust(newCategory, Today.AddDays(-1), "Vet visit", 9900, flock, "invoice 42");

        Assert.True(result.IsSuccess);
        Assert.Equal(newCategory, e.ExpenseCategoryId);
        Assert.Equal("Vet visit", e.Description);
        Assert.Equal(9900, e.AmountMinorUnits);
        Assert.Equal(flock, e.FlockId);
        Assert.Equal("invoice 42", e.Note);
        Assert.Equal(1, e.Version);
        Assert.Equal("USD", e.CurrencyCode); // never re-denominates
    }

    [Fact]
    public void Adjust_InvalidAmount_FailsWithoutMutating()
    {
        var e = Make(1500);
        var result = e.Adjust(e.ExpenseCategoryId, Today, "x", 0, null, null);
        Assert.True(result.IsFailure);
        Assert.Equal("Expense.AmountNotPositive", result.Error.Code);
        Assert.Equal(1500, e.AmountMinorUnits);
        Assert.Equal(0, e.Version);
    }

    [Fact]
    public void Adjust_LongNote_Fails()
    {
        var e = Make();
        var result = e.Adjust(e.ExpenseCategoryId, Today, "ok", 100, null, new string('x', Expense.MaxNoteLength + 1));
        Assert.True(result.IsFailure);
        Assert.Equal("Expense.NoteTooLong", result.Error.Code);
    }
}

public sealed class ExpenseCategoryTests
{
    private static ExpenseCategory Make(string name = "Feed") =>
        ExpenseCategory.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), name);

    [Fact]
    public void Create_Trims_StartsActive()
    {
        var c = Make("  Feed ");
        Assert.Equal("Feed", c.Name);
        Assert.True(c.Active);
        Assert.Equal(0, c.Version);
    }

    [Fact]
    public void Rename_Blank_Fails()
    {
        var c = Make();
        var result = c.Rename("  ");
        Assert.True(result.IsFailure);
        Assert.Equal("ExpenseCategory.NameRequired", result.Error.Code);
    }

    [Fact]
    public void DeactivateActivate_RoundTrip_GuardsDoubleCalls()
    {
        var c = Make();
        Assert.True(c.Deactivate().IsSuccess);
        Assert.Equal("ExpenseCategory.NotActive", c.Deactivate().Error.Code);
        Assert.True(c.Activate().IsSuccess);
        Assert.Equal("ExpenseCategory.AlreadyActive", c.Activate().Error.Code);
        Assert.Equal(2, c.Version);
    }
}
