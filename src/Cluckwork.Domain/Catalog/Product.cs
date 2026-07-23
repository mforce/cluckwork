namespace Cluckwork.Domain.Catalog;

using Cluckwork.Domain.Common;

// Product catalog (spec §10.1) — what the farm sells. Phase 1 only egg
// products are creatable (they map to an egg grade via
// ProductEggGradeMapping); the enum already carries the future types so the
// schema doesn't churn when live birds / meat / services arrive.
public sealed class Product : AggregateRoot<Guid>
{
    public const int MaxNameLength = 100;
    public const int MaxNotesLength = 500;

    public Guid FarmId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public ProductType ProductType { get; private set; }
    public ProductUnit DefaultUnit { get; private set; }
    public long? DefaultPriceMinorUnits { get; private set; }
    // Snapshot from the account at creation (spec §16 currency rule) — a later
    // currency change never re-denominates the catalog.
    public string CurrencyCode { get; private set; } = string.Empty;
    public int CurrencyMinorUnit { get; private set; }
    public string? Notes { get; private set; }
    public bool Active { get; private set; }
    public int Version { get; private set; }

    private Product() { }

    public static Product Create(
        Guid id, Guid accountId, Guid farmId, string name, ProductType productType,
        ProductUnit defaultUnit, long? defaultPriceMinorUnits,
        string currencyCode, int currencyMinorUnit, string? notes)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Product name is required.", nameof(name));
        if (name.Trim().Length > MaxNameLength)
            throw new ArgumentException($"Product name cannot exceed {MaxNameLength} characters.", nameof(name));
        if (defaultPriceMinorUnits is < 0)
            throw new ArgumentException("Default price cannot be negative.", nameof(defaultPriceMinorUnits));

        return new Product
        {
            Id = id, AccountId = accountId, FarmId = farmId,
            Name = name.Trim(),
            ProductType = productType,
            DefaultUnit = defaultUnit,
            DefaultPriceMinorUnits = defaultPriceMinorUnits,
            CurrencyCode = currencyCode,
            CurrencyMinorUnit = currencyMinorUnit,
            Notes = Truncate(notes),
            Active = true
        };
    }

    // ProductType stays immutable — sold lines will snapshot it (spec §10.5),
    // and retyping a product would silently reinterpret what was sold.
    public Result Update(
        string name, ProductUnit defaultUnit, long? defaultPriceMinorUnits, string? notes,
        string farmCurrencyCode, int farmCurrencyMinorUnit)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure(Error.Validation("Product.NameRequired", "Product name is required."));
        if (name.Trim().Length > MaxNameLength)
            return Result.Failure(Error.Validation(
                "Product.NameTooLong", $"Product name cannot exceed {MaxNameLength} characters."));
        if (defaultPriceMinorUnits is < 0)
            return Result.Failure(Error.Validation(
                "Product.NegativePrice", "Default price cannot be negative."));

        // An UNPRICED product still carries the currency it was created under,
        // and it does not lock the farm's currency (§4.6 / #123 — nothing reads
        // its currency as an amount), so the farm may have moved on since. Its
        // FIRST price therefore binds to the currency the farm uses now;
        // otherwise every order taking that default would be refused as a
        // cross-currency price with no way to fix it through the API.
        //
        // An already-priced product is never re-stamped: that would silently
        // re-denominate a real price. It cannot need to be, either — a priced
        // product locks the farm currency, so the two already agree.
        if (DefaultPriceMinorUnits is null && defaultPriceMinorUnits is not null)
        {
            CurrencyCode = farmCurrencyCode;
            CurrencyMinorUnit = farmCurrencyMinorUnit;
        }

        Name = name.Trim();
        DefaultUnit = defaultUnit;
        DefaultPriceMinorUnits = defaultPriceMinorUnits;
        Notes = Truncate(notes);
        Version++;
        return Result.Success();
    }

    public Result Deactivate()
    {
        if (!Active)
            return Result.Failure(Error.Domain("Product.NotActive", "Product is already inactive."));
        Active = false;
        Version++;
        return Result.Success();
    }

    public Result Activate()
    {
        if (Active)
            return Result.Failure(Error.Domain("Product.AlreadyActive", "Product is already active."));
        Active = true;
        Version++;
        return Result.Success();
    }

    private static string? Truncate(string? notes)
    {
        var trimmed = notes?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length <= MaxNotesLength ? trimmed : trimmed[..MaxNotesLength];
    }
}

public enum ProductType { Egg, LiveBird, Meat, Chick, Pullet, Manure, Service, Other }

// Spec §10.1 default_unit values. "Egg" is the individual egg (maps to the
// "Individual" conversion row); packed units resolve through
// EggUnitConversion at sale time (part 2).
public enum ProductUnit { Egg, Dozen, Flat, Tray, Carton, Case, Bird, Lb, Kg, Package, Other }
