using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Commerce.Catalog;
using Cluckwork.Application.Modules.Commerce.Contracts;
using Cluckwork.Application.Modules.Commerce.Sales;
using Cluckwork.Application.Modules.Commerce.Sales.AddOrderItem;
using Cluckwork.Application.Modules.EggOperations.Contracts;
using Cluckwork.Domain.Modules.Commerce.Catalog;
using Cluckwork.Domain.Modules.Commerce.Contracts;
using Cluckwork.Domain.Modules.Commerce.Sales;
using Cluckwork.Domain.Modules.EggOperations.Contracts;

namespace Cluckwork.Application.Tests.Sales;

// #1160 — a product's default price is per its OWN selling unit, so a line in
// another unit defaults to, and snapshots as its list price, that price scaled
// by the two eggs-per-unit factors.
public sealed class AddOrderItemHandlerTests
{
    private static readonly Guid AccountId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid ProductId = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    private static readonly Guid GradeId = Guid.Parse("00000000-0000-0000-0000-0000000000c1");

    private readonly FakeOrders _orders = new();
    private readonly FakeProducts _products = new();
    private readonly FakeConversions _conversions = new();
    private readonly SalesOrder _order = SalesOrder.Create(
        Guid.NewGuid(), AccountId, Guid.NewGuid(), "SO-1", new DateOnly(2026, 10, 10), "USD");

    public AddOrderItemHandlerTests()
    {
        _orders.Order = _order;
        _conversions.Rows.AddRange(
        [
            EggUnitConversion.Create(Guid.NewGuid(), AccountId, EggUnit.Individual, 1),
            EggUnitConversion.Create(Guid.NewGuid(), AccountId, EggUnit.Dozen, 12),
            EggUnitConversion.Create(Guid.NewGuid(), AccountId, EggUnit.Tray, 30),
            EggUnitConversion.Create(Guid.NewGuid(), AccountId, EggUnit.Case, 360),
        ]);
    }

    private AddOrderItemHandler Handler() => new(
        _orders, _products, new FakeGrades(), _conversions, new FakeAudit(), new FakeUnitOfWork());

    private void SeedProduct(ProductUnit unit, long? price) =>
        _products.Product = Product.Create(
            ProductId, AccountId, Guid.NewGuid(), "Large Eggs", ProductType.Egg, unit, price, "USD", 2, null);

    private Task<Cluckwork.Domain.Common.Result<Guid>> AddAsync(
        ProductUnit unit, long? price = null, long? expectedListPrice = null) =>
        Handler().HandleAsync(
            new AddOrderItemCommand(_order.Id, ProductId, 8, unit.ToString(), price,
                ExpectedListUnitPriceMinorUnits: expectedListPrice),
            AccountId, default);

    [Theory]
    // The issue's line: Large Eggs at 0.45 per egg, sold per tray of 30.
    [InlineData(ProductUnit.Egg, 45L, ProductUnit.Tray, 1_350L)]
    // The reverse: a tray price on a per-egg line, 30 times too high before.
    [InlineData(ProductUnit.Tray, 1_350L, ProductUnit.Egg, 45L)]
    // Packed unit to packed unit: 6.00 a dozen is 15.00 a tray.
    [InlineData(ProductUnit.Dozen, 600L, ProductUnit.Tray, 1_500L)]
    // Uneven scaling rounds UP to a whole minor unit: 43.33 and 43.5 both
    // become 44, and a positive price never scales to a free line (0.36 → 1).
    [InlineData(ProductUnit.Tray, 1_300L, ProductUnit.Egg, 44L)]
    [InlineData(ProductUnit.Tray, 1_305L, ProductUnit.Egg, 44L)]
    [InlineData(ProductUnit.Case, 130L, ProductUnit.Egg, 1L)]
    // A genuinely free product stays free.
    [InlineData(ProductUnit.Case, 0L, ProductUnit.Egg, 0L)]
    // The product's own unit is untouched.
    [InlineData(ProductUnit.Tray, 1_300L, ProductUnit.Tray, 1_300L)]
    public async Task OmittedPrice_DefaultsAndSnapshotsTheListPriceInTheLinesUnit(
        ProductUnit productUnit, long productPrice, ProductUnit lineUnit, long expected)
    {
        SeedProduct(productUnit, productPrice);

        Assert.True((await AddAsync(lineUnit)).IsSuccess);

        var item = _order.Items.Single();
        Assert.Equal(expected, item.UnitPrice.MinorUnits);
        Assert.Equal(expected, item.ListUnitPriceMinorUnits);
        Assert.Equal(ListPriceBasis.Recorded, item.ListPriceBasis);
        Assert.Equal(8 * expected, _order.TotalAmount.MinorUnits);
    }

    [Fact]
    public async Task ExplicitPrice_StillSnapshotsTheListPriceInTheLinesUnit()
    {
        SeedProduct(ProductUnit.Egg, 45);

        Assert.True((await AddAsync(ProductUnit.Tray, price: 1_200)).IsSuccess);

        var item = _order.Items.Single();
        Assert.Equal(1_200, item.UnitPrice.MinorUnits);
        Assert.Equal(1_350, item.ListUnitPriceMinorUnits);
    }

    [Fact]
    public async Task ExpectedListPrice_IsComparedInTheLinesUnit()
    {
        SeedProduct(ProductUnit.Egg, 45);

        var unscaled = await AddAsync(ProductUnit.Tray, price: 1_350, expectedListPrice: 45);
        Assert.Equal("SalesOrder.ListPriceChanged", unscaled.Error.Code);
        Assert.Contains("now 1350, not 45", unscaled.Error.Description);

        Assert.True((await AddAsync(ProductUnit.Tray, price: 1_350, expectedListPrice: 1_350)).IsSuccess);
    }

    [Fact]
    public async Task ProductUnitWithoutAnActiveDefinition_RefusesALineInAnotherUnit()
    {
        SeedProduct(ProductUnit.Dozen, 600);
        Assert.True(_conversions.Rows.Single(c => c.UnitCode == EggUnit.Dozen).Update(12, false).IsSuccess);

        var result = await AddAsync(ProductUnit.Tray);

        Assert.Equal("SalesOrder.NoUnitConversion", result.Error.Code);
        Assert.Contains("'Dozen'", result.Error.Description);
        Assert.Empty(_order.Items);
    }

    [Fact]
    public async Task ScaledPriceBeyondTheSupportedRange_IsRefused()
    {
        SeedProduct(ProductUnit.Egg, long.MaxValue / 16);

        var result = await AddAsync(ProductUnit.Tray);

        Assert.Equal("SalesOrder.LineTotalTooLarge", result.Error.Code);
        Assert.Empty(_order.Items);
    }

    private sealed class FakeOrders : ISalesOrderRepository
    {
        public SalesOrder? Order { get; set; }

        public Task<SalesOrder?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(Order?.Id == id ? Order : null);

        public Task AddAsync(SalesOrder entity, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SalesOrder?> GetReadOnlyAsync(Guid id, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<SalesOrder?> GetByIdLockedAsync(Guid accountId, Guid id, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<SalesOrderListRow>> ListAsync(
            SalesOrderListFilter filter, int limit, int offset, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeProducts : IProductRepository
    {
        public Product? Product { get; set; }

        public Task<Product?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(Product?.Id == id ? Product : null);

        public Task<ProductEggGradeMapping?> GetMappingAsync(Guid productId, CancellationToken ct = default) =>
            Task.FromResult<ProductEggGradeMapping?>(
                ProductEggGradeMapping.Create(Guid.NewGuid(), AccountId, productId, GradeId));

        public Task<IReadOnlyList<Product>> ListAsync(bool includeInactive, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> NameExistsAsync(string name, Guid? excludeId = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task AddAsync(Product product, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProductEggGradeMapping>> ListMappingsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task AddMappingAsync(ProductEggGradeMapping mapping, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeConversions : IEggUnitConversionRepository
    {
        public List<EggUnitConversion> Rows { get; } = [];

        public Task<EggUnitConversion?> GetByUnitAsync(EggUnit unit, CancellationToken ct = default) =>
            Task.FromResult(Rows.SingleOrDefault(c => c.UnitCode == unit));

        public Task<EggUnitConversion?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<EggUnitConversion>> ListAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeGrades : IEggGradeLookup
    {
        public Task<EggGradeDetails?> GetAsync(Guid id, CancellationToken ct) =>
            Task.FromResult<EggGradeDetails?>(new EggGradeDetails(
                id, Guid.NewGuid(), "Large", EggGradeType.Size, 1, true, DailyEntryKind.Manual, true, null));

        public Task<IReadOnlyDictionary<Guid, string>> GetDisplayNamesAsync(
            IReadOnlyCollection<Guid> gradeIds, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeAudit : IAuditWriter
    {
        public Task WriteAsync(
            string action, string entityType, Guid entityId,
            string? reason = null, object? details = null, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeUnitOfWork : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken ct = default) => Task.FromResult(1);

        public Task<bool> ExecuteInTransactionAsync(
            Func<CancellationToken, Task<bool>> operation, CancellationToken ct = default) => operation(ct);
    }
}
