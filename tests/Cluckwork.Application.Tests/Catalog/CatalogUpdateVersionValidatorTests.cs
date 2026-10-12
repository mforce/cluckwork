using Cluckwork.Application.Modules.Commerce.Catalog.UpdateEggUnitConversion;
using Cluckwork.Application.Modules.Commerce.Catalog.UpdateProduct;
using Cluckwork.Application.Modules.Commerce.Contracts;

namespace Cluckwork.Application.Tests.Catalog;

// Product and packed-unit edits carry the base Version the client loaded. An
// omitted Version binds to null and must fail its own code, never pass as 0.
public sealed class CatalogUpdateVersionValidatorTests
{
    private static UpdateProductCommand Product(int? version) =>
        new(Guid.NewGuid(), version, "Large Eggs", "Dozen", 450, Guid.NewGuid(), null);

    private static UpdateEggUnitConversionCommand Conversion(int? version) =>
        new(Guid.NewGuid(), version, 12, true);

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task Product_PresentVersion_Passes(int version) =>
        Assert.True((await new UpdateProductValidator().ValidateAsync(Product(version))).IsValid);

    [Theory]
    [InlineData(null, "Product.Version.Required")]
    [InlineData(-1, "Product.Version.NonNegative")]
    public async Task Product_MissingOrNegativeVersion_Fails(int? version, string code)
    {
        var result = await new UpdateProductValidator().ValidateAsync(Product(version));
        Assert.Equal([code], result.Errors.Select(e => e.ErrorCode));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task Conversion_PresentVersion_Passes(int version) =>
        Assert.True((await new UpdateEggUnitConversionValidator().ValidateAsync(Conversion(version))).IsValid);

    [Theory]
    [InlineData(null, "EggUnitConversion.Version.Required")]
    [InlineData(-1, "EggUnitConversion.Version.NonNegative")]
    public async Task Conversion_MissingOrNegativeVersion_Fails(int? version, string code)
    {
        var result = await new UpdateEggUnitConversionValidator().ValidateAsync(Conversion(version));
        Assert.Equal([code], result.Errors.Select(e => e.ErrorCode));
    }
}
