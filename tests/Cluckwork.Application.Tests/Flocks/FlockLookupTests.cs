using Cluckwork.Application.Features.Flocks;
using Cluckwork.Domain.Flocks;

namespace Cluckwork.Application.Tests.Flocks;

// #852: the lookup copies the flock field by field into a positional record,
// where two ids or two dates can swap without a compile error.
public sealed class FlockLookupTests
{
    private static readonly Guid FlockId = Guid.Parse("00000000-0000-0000-0000-0000000000f1");
    private static readonly Guid AccountId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid FarmId = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    private static readonly Guid HouseId = Guid.Parse("00000000-0000-0000-0000-0000000000c1");

    private static Flock DepletedThenArchived()
    {
        var flock = Flock.Create(FlockId, AccountId, FarmId, HouseId, "House 1", "ISA Brown", new DateOnly(2026, 1, 5), 500);
        Assert.True(flock.Deplete(new DateOnly(2026, 8, 1)).IsSuccess);
        Assert.True(flock.Archive(new DateOnly(2026, 9, 1)).IsSuccess);
        return flock;
    }

    private static readonly FlockDetails Expected = new(
        FlockId, FarmId, HouseId, "House 1", "ISA Brown", new DateOnly(2026, 1, 5), 500,
        FlockStatus.Archived, new DateOnly(2026, 8, 1), new DateOnly(2026, 9, 1), 2);

    [Fact]
    public async Task Get_CopiesEveryField() =>
        Assert.Equal(Expected, await new FlockLookup(new StubFlocks(DepletedThenArchived())).GetAsync(FlockId, default));

    [Fact]
    public async Task GetForFlockScopedWrite_CopiesEveryField() =>
        Assert.Equal(Expected, await new FlockLookup(new StubFlocks(DepletedThenArchived()))
            .GetForFlockScopedWriteAsync(FlockId, AccountId, default));

    [Fact]
    public async Task Get_UnknownFlockIsNull() =>
        Assert.Null(await new FlockLookup(new StubFlocks(null)).GetAsync(FlockId, default));

    [Theory]
    [InlineData("2026-07-31", true)]
    [InlineData("2026-08-01", true)]
    [InlineData("2026-08-02", false)]
    public void CanRecordProductionOn_DepletedFlock_AcceptsOnlyDatesUpToDepletion(string date, bool expected)
    {
        var depleted = Expected with { Status = FlockStatus.Depleted, ArchivedOn = null };
        Assert.Equal(expected, depleted.CanRecordProductionOn(DateOnly.Parse(date)));
    }

    [Fact]
    public void CanRecordProductionOn_ArchivedFlock_AcceptsNothing() =>
        Assert.False(Expected.CanRecordProductionOn(new DateOnly(2026, 7, 1)));

    [Fact]
    public async Task ResolveByName_NoMatchIsNotFound() =>
        Assert.IsType<FlockNameResolution.NotFound>(
            await new FlockLookup(new StubFlocks(null, [])).ResolveByNameAsync("House 1", default));

    [Fact]
    public async Task ResolveByName_OneMatchIsFound()
    {
        var only = new FlockReference(FlockId, "House 1", FlockStatus.Active);
        Assert.Equal(new FlockNameResolution.Found(only),
            await new FlockLookup(new StubFlocks(null, [only])).ResolveByNameAsync("House 1", default));
    }

    [Fact]
    public async Task ResolveByName_TwoMatchesAreAmbiguousWithBothCandidates()
    {
        FlockReference[] both =
        [
            new(FlockId, "House 1", FlockStatus.Active),
            new(Guid.Parse("00000000-0000-0000-0000-0000000000f2"), "House 1", FlockStatus.Depleted),
        ];
        var resolution = await new FlockLookup(new StubFlocks(null, both)).ResolveByNameAsync("House 1", default);
        Assert.Equal(both, Assert.IsType<FlockNameResolution.Ambiguous>(resolution).Candidates);
    }

    private sealed class StubFlocks(Flock? flock, IReadOnlyList<FlockReference>? byName = null) : IFlockRepository
    {
        public Task<Flock?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult(flock);

        public Task<Flock?> GetByIdForFlockScopedWriteAsync(Guid id, Guid accountId, CancellationToken ct = default) =>
            Task.FromResult(flock);

        public Task<IReadOnlyList<FlockReference>> ListByNameAsync(string name, CancellationToken ct = default) =>
            Task.FromResult(byName ?? throw new NotSupportedException());

        public Task<IReadOnlyList<Flock>> ListAsync(int limit, int offset, bool includeArchived = false, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<Flock>> SearchAsync(string? search, FlockEligibility eligibility, int limit, int offset, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyDictionary<Guid, FlockReference>> GetDisplayNamesAsync(IReadOnlyCollection<Guid> flockIds, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task AddAsync(Flock entity, CancellationToken ct = default) => throw new NotSupportedException();
        public void Update(Flock entity) => throw new NotSupportedException();
        public void Remove(Flock entity) => throw new NotSupportedException();
    }
}
