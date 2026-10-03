namespace Cluckwork.Application.Features.Accounts;

// #854: one table that may hold an amount in the farm currency (spec §4.6).
// The module that owns the table implements it, so Farm asks without reading
// another module's tables. A table that starts holding amounts registers one.
public interface ICurrencyBoundRowSource
{
    Task<bool> AnyAsync(CancellationToken ct);
}
