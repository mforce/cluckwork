namespace Cluckwork.Domain.Accounts;

// Spec §4.5 farms.unit_system — default display units for feed, water and
// weights. Stored quantities keep their own recorded unit; this only picks the
// default the capture forms offer.
public enum UnitSystem
{
    Metric = 0,
    Imperial = 1
}
