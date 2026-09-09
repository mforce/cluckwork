namespace Cluckwork.Application.Features.Sales.AddOrderItem;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Catalog;
using Cluckwork.Application.Features.EggGrades;
using Cluckwork.Application.Features.Sales;
using Cluckwork.Domain.Catalog;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Sales;

public sealed class AddOrderItemHandler(
    ISalesOrderRepository orders,
    IProductRepository products,
    IEggGradeRepository eggGrades,
    IEggUnitConversionRepository conversions,
    IAuditWriter audit,
    IUnitOfWork unitOfWork)
{
    public async Task<Result<Guid>> HandleAsync(
        AddOrderItemCommand command, Guid accountId, CancellationToken ct)
    {
        var order = await orders.GetByIdAsync(command.SalesOrderId, ct);
        if (order is null)
            return Result.Failure<Guid>(Error.NotFound(nameof(SalesOrder), command.SalesOrderId));

        // Lines sell PRODUCTS (spec §10.5). Tenant-scoped by the query filter.
        var product = await products.GetByIdAsync(command.ProductId, ct);
        if (product is null || !product.Active)
            return Result.Failure<Guid>(Error.Validation(
                "SalesOrder.UnknownProduct", "The product does not exist or is inactive."));
        if (product.ProductType != ProductType.Egg)
            return Result.Failure<Guid>(Error.Validation(
                "SalesOrder.NotAnEggProduct", "Only egg products can be sold in this phase."));

        // The grade snapshot comes from the product's CURRENT mapping — the
        // line keeps it even if the mapping is re-pointed later.
        var mapping = await products.GetMappingAsync(product.Id, ct);
        var grade = mapping is null ? null : await eggGrades.GetByIdAsync(mapping.EggGradeId, ct);
        if (grade is null || !grade.Active || !grade.IsSaleable)
            return Result.Failure<Guid>(Error.Validation(
                "SalesOrder.UnknownGrade",
                "The product's egg grade does not exist, is inactive, or is not saleable."));

        // Factor resolves from the account's ACTIVE conversion at line creation
        // and is snapshotted (spec §9.7) — a later carton redefinition never
        // reinterprets this line.
        var unit = command.Unit is { } u
            ? Enum.Parse<ProductUnit>(u, ignoreCase: true)
            : product.DefaultUnit;
        var conversion = await conversions.GetByUnitAsync(EggUnits.ToConversionUnit(unit), ct);
        if (conversion is null || !conversion.Active)
            return Result.Failure<Guid>(Error.Validation(
                "SalesOrder.NoUnitConversion",
                $"No active eggs-per-unit definition for '{unit}' — set one on the Products screen."));

        // #445 — the caller previewed "= N eggs" from a factor it read
        // earlier; if the definition changed in between, snapshotting the new
        // one would silently record a different QuantityBase than the user
        // saw. Refuse instead — the user re-checks against the new factor.
        if (command.ExpectedEggsPerUnit is { } expected && expected != conversion.EggsPerUnit)
            return Result.Failure<Guid>(Error.Validation(
                "SalesOrder.UnitDefinitionChanged",
                $"The eggs-per-unit definition for '{unit}' is now {conversion.EggsPerUnit}, not {expected} — " +
                "re-check the quantity and try again."));

        // #720 — "did the catalogue move under the seller?" A bare long? cannot
        // tell OMITTED (no opinion: raw API callers, both seeders) from
        // EXPECTED-UNSET (the seller looked and saw no list price). Zero is a
        // legal list price, so it cannot be a sentinel. Hence the companion
        // flag: an expectation exists when either is present, and then the
        // comparison runs on the raw nullable values, so ALL FOUR transitions
        // are covered — unchanged, number→number, number→null, and null→number.
        // That last one is the case this guard originally missed: a seller who
        // saw "No list price" while an admin was pricing the product would
        // otherwise have the line snapshot a number nobody had shown them.
        var listPriceExpectationGiven =
            command.ExpectedListUnitPriceMinorUnits is not null
            || command.ExpectedListPriceIsUnset;
        if (listPriceExpectationGiven
            && command.ExpectedListUnitPriceMinorUnits != product.DefaultPriceMinorUnits)
            return Result.Failure<Guid>(Error.Validation(
                "SalesOrder.ListPriceChanged",
                $"This product's list price is now " +
                $"{(product.DefaultPriceMinorUnits?.ToString() ?? "unset")}, not " +
                $"{(command.ExpectedListUnitPriceMinorUnits?.ToString() ?? "unset")} — " +
                "re-check the price and try again."));

        // Price defaults from the product (per selling unit).
        var priceMinorUnits = command.UnitPriceMinorUnits ?? product.DefaultPriceMinorUnits;
        if (priceMinorUnits is null)
            return Result.Failure<Guid>(Error.Validation(
                "SalesOrder.PriceRequired",
                "The product has no default price — a unit price is required."));

        // A catalog price is a raw minor-unit integer in the currency the
        // product snapshotted, and the line below stamps it with the ORDER's
        // currency. If those ever differ, $12.34 (1234) silently becomes
        // ¥1,234 — the same number read a hundred times too large. #123's
        // currency lock is what keeps them equal; this is the backstop that
        // refuses rather than mis-prices if anything ever gets past it.
        // An explicitly supplied price is the caller's own number in the
        // order's currency, so it is unaffected.
        if (command.UnitPriceMinorUnits is null
            && !string.Equals(product.CurrencyCode, order.TotalAmount.CurrencyCode, StringComparison.OrdinalIgnoreCase))
            return Result.Failure<Guid>(Error.Validation(
                "SalesOrder.ProductPriceCurrencyMismatch",
                $"This product's default price is in {product.CurrencyCode} but the order is in " +
                $"{order.TotalAmount.CurrencyCode}. Re-price the product or enter a unit price."));
        // The validator's overflow guard only sees an explicit price — the
        // product-default path needs the same check (Money.Multiply is unchecked).
        if (priceMinorUnits.Value > long.MaxValue / command.Quantity)
            return Result.Failure<Guid>(Error.Validation(
                "SalesOrder.LineTotalTooLarge", "Line total exceeds the supported amount range."));

        // Item price inherits the order's snapshotted currency.
        var unitPrice = new Money(
            priceMinorUnits.Value,
            order.TotalAmount.CurrencyCode,
            order.TotalAmount.CurrencyMinorUnit);

        // #720 — the LIST price this line was sold against, snapshotted so a
        // later catalogue re-price can never reinterpret a recorded order
        // (spec §10.5 — the rule BaseUnitFactor already follows).
        //
        // Recorded ONLY when the product's denomination matches the order's:
        // the same currency CODE and the same MINOR UNIT. The column is a bare
        // long? with no currency of its own, so it is meaningful only if the
        // line's UnitPrice columns describe it — and they do exactly when this
        // condition holds. On a mismatch we store NULL: "no comparable list
        // price" is a true statement, where the raw integer would be a number
        // in an unknown denomination.
        //
        // Note the minor-unit half. The guard above compares CODE only, which
        // is the gap SalesPage.tsx:191-203 records: a prefill 100x out,
        // arriving as an EXPLICIT price, on the one path that guard skips.
        // #123's currency lock makes a mismatch unreachable through the API
        // today; this is recorded history, so "unreachable" is not enough.
        // #720 — the value and its BASIS are decided together, in one expression,
        // so they cannot disagree. Every branch below is reachable: an unpriced
        // product is legal, and the denomination branch is the backstop #123's
        // currency lock makes unreachable through the API today.
        var (listUnitPriceMinorUnits, listPriceBasis) =
            product.DefaultPriceMinorUnits is not { } catalogListPrice
                ? ((long?)null, ListPriceBasis.ProductUnpriced)
                : !string.Equals(
                      product.CurrencyCode,
                      order.TotalAmount.CurrencyCode,
                      StringComparison.OrdinalIgnoreCase)
                  || product.CurrencyMinorUnit != order.TotalAmount.CurrencyMinorUnit
                    ? ((long?)null, ListPriceBasis.NotComparable)
                    : (catalogListPrice, ListPriceBasis.Recorded);

        var result = order.AddItem(
            product.Id, product.ProductType, grade.Id,
            unit, conversion.EggsPerUnit, command.Quantity, unitPrice,
            listUnitPriceMinorUnits, listPriceBasis);
        if (result.IsFailure)
            return Result.Failure<Guid>(result.Error);

        // #494 — see RemoveOrderItemHandler: draft-only, recorded for attribution.
        await audit.WriteAsync(AuditActions.SalesOrderAddItem, nameof(SalesOrder), order.Id, ct: ct);

        // EF assigns the item id during save (deliberately not client-set).
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success(result.Value.Id);
    }
}
