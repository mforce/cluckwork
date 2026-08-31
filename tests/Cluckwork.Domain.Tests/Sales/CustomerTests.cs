namespace Cluckwork.Domain.Tests.Sales;

using Cluckwork.Domain.Sales;

public sealed class CustomerTests
{
    private static Customer Make() =>
        Customer.Create(Guid.NewGuid(), Guid.NewGuid(), "Original Name", "555-0000");

    [Fact]
    public void Update_TrimsAndNormalizes_BumpsVersionOnce()
    {
        var c = Make();

        var result = c.Update("  New Name  ", "  555-1111  ", "   ", "   ", "   ");

        Assert.True(result.IsSuccess);
        Assert.Equal("New Name", c.Name);
        Assert.Equal("555-1111", c.Phone);
        Assert.Null(c.Email);
        Assert.Null(c.Address);
        Assert.Null(c.Note);
        Assert.Equal(1, c.Version);
    }

    [Fact]
    public void Update_BlankName_Fails_DoesNotMutateOrBumpVersion()
    {
        var c = Make();

        var result = c.Update("   ", "555-1111", null, null, null);

        Assert.True(result.IsFailure);
        Assert.Equal("Customer.Name.Required", result.Error.Code);
        Assert.Equal("Original Name", c.Name);
        Assert.Equal("555-0000", c.Phone);
        Assert.Equal(0, c.Version);
    }

    [Fact]
    public void Update_BlankPhone_Fails_DoesNotMutateOrBumpVersion()
    {
        var c = Make();

        var result = c.Update("New Name", "   ", null, null, null);

        Assert.True(result.IsFailure);
        Assert.Equal("Customer.Phone.Required", result.Error.Code);
        Assert.Equal("Original Name", c.Name);
        Assert.Equal("555-0000", c.Phone);
        Assert.Equal(0, c.Version);
    }

    [Fact]
    public void Update_SameValues_StillSucceeds_BumpsVersionExactlyOnce()
    {
        var c = Make();

        var result = c.Update("Original Name", "555-0000", null, null, null);

        Assert.True(result.IsSuccess);
        Assert.Equal("Original Name", c.Name);
        Assert.Equal("555-0000", c.Phone);
        Assert.Equal(1, c.Version);
    }
}
