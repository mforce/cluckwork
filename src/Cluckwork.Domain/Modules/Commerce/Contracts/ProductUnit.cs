namespace Cluckwork.Domain.Modules.Commerce.Contracts;

// Spec §10.1 default_unit values. "Egg" is the individual egg (maps to the
// "Individual" conversion row); packed units resolve through
// EggUnitConversion at sale time (part 2).
public enum ProductUnit { Egg, Dozen, Flat, Tray, Carton, Case, Bird, Lb, Kg, Package, Other }
