namespace Cluckwork.Application.Features.Accounts;

// Reads across every farm, with no tenant resolved, for the operator verbs and
// the lock sweep (#858). Each query ignores the tenant filter, so no request
// path may reach it. FarmDirectoryCallerTests only keeps this name out of
// request code; it does not follow a forwarder in an allowed place, which today
// is AccountSlugLookup.ResolveAsync.
public interface IFarmDirectory
{
    // Every farm, suspended ones included: the lock sweep still locks their
    // entries.
    Task<IReadOnlyList<FarmTimeZone>> ListTimeZonesAsync(CancellationToken ct = default);

    Task<IReadOnlyList<FarmListing>> ListAsync(CancellationToken ct = default);

    // The caller normalizes the code. Null unless exactly one farm matches.
    Task<Guid?> FindIdBySlugAsync(string slug, CancellationToken ct = default);
}

public sealed record FarmTimeZone(Guid AccountId, string TimeZoneId);

public sealed record FarmListing(string Slug, string Name, bool IsActive);
