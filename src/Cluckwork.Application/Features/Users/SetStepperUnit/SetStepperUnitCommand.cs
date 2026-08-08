namespace Cluckwork.Application.Features.Users.SetStepperUnit;

// The requested EggUnit's name (e.g. "Tray"), or null to clear the override and
// follow the farm default again. Travels as a string like every other
// enum-by-name field in this API (UnitSystem, FirstDayOfWeek) — the validator
// proves it names a defined EggUnit before the handler parses it.
public sealed record SetStepperUnitCommand(string? Unit);
