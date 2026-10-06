namespace Cluckwork.Application.Modules.Farm.Accounts;

// #854: everything that has snapshotted the farm currency, asked of the
// modules that own it, scoped to the current tenant by their query filters.
// Sources answer in registration order and the first yes ends the probe, so
// on a working farm the first one usually answers.
public sealed class CurrencyBoundRowProbe(IEnumerable<ICurrencyBoundRowSource> sources) : ICurrencyBoundRowProbe
{
    public async Task<bool> AnyAsync(CancellationToken ct = default)
    {
        foreach (var source in sources)
        {
            if (await source.AnyAsync(ct))
                return true;
        }
        return false;
    }
}
