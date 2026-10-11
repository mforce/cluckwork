using Cluckwork.Api.Mcp;
using ModelContextProtocol;

namespace Cluckwork.Api.IntegrationTests.Mcp;

// #807 F6 — the page builder alone, with a fake query: the window it asks for, and the
// completeness and continuation it reports from what comes back.
public sealed class McpPageTests
{
    [Theory]
    [InlineData(-5, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(100, 100)]
    [InlineData(500, McpPage.MaxLimit)]
    public async Task AsksForOneRowPastTheClampedLimit(int limit, int take)
    {
        var asked = new List<(int Take, int Skip)>();

        await McpPage<int>.ReadAsync(limit, 0, Rows(asked, 0), r => r, default);

        Assert.Equal([(take + 1, 0)], asked);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1_000_000, 1_000_000)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public async Task KeepsANonnegativeOffset(int offset, int skip)
    {
        var asked = new List<(int Take, int Skip)>();

        await McpPage<int>.ReadAsync(10, offset, Rows(asked, 0), r => r, default);

        Assert.Equal([(11, skip)], asked);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(10)]
    public async Task UpToTheLimit_IsComplete(int available)
    {
        var page = await McpPage<int>.ReadAsync(10, 30, Rows([], available), r => r, default);

        Assert.Equal(available, page.Items.Count);
        Assert.True(page.Complete);
        Assert.Null(page.NextOffset);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(30, 40)]
    public async Task MoreThanTheLimit_KeepsThePage_AndContinuesAfterIt(int offset, int next)
    {
        var page = await McpPage<int>.ReadAsync(10, offset, Rows([], 11), r => r, default);

        Assert.Equal(Enumerable.Range(0, 10), page.Items);
        Assert.False(page.Complete);
        Assert.Equal(next, page.NextOffset);
    }

    // Near int.MaxValue a continuation is followed, never moved backwards, until the next
    // offset no longer fits in an int; then the call is refused rather than repeated.
    [Fact]
    public async Task NearTheIntegerLimit_ContinuesForward_ThenRefuses()
    {
        var asked = new List<(int Take, int Skip)>();
        Func<int, int, CancellationToken, Task<IReadOnlyList<long>>> endless = (take, skip, _) =>
        {
            asked.Add((take, skip));
            return Task.FromResult<IReadOnlyList<long>>([.. Enumerable.Range(0, take).Select(i => (long)skip + i)]);
        };

        var first = await McpPage<long>.ReadAsync(100, int.MaxValue - 150, endless, r => r, default);
        var refused = await Assert.ThrowsAsync<McpException>(() =>
            McpPage<long>.ReadAsync(100, first.NextOffset!.Value, endless, r => r, default));

        Assert.Equal(int.MaxValue - 50, first.NextOffset);
        Assert.Equal((long)int.MaxValue - 150, first.Items[0]);
        Assert.Equal([(101, int.MaxValue - 150), (101, int.MaxValue - 50)], asked);
        Assert.Contains("more rows after it than an offset can reach", refused.Message);
    }

    [Fact]
    public async Task MapsEveryKeptRow()
    {
        var page = await McpPage<string>.ReadAsync(2, 0, Rows([], 3), r => $"row {r}", default);

        Assert.Equal(["row 0", "row 1"], page.Items);
    }

    // A query that answers with up to take rows, from a source holding `available`.
    private static Func<int, int, CancellationToken, Task<IReadOnlyList<int>>> Rows(
        List<(int Take, int Skip)> asked, int available) =>
        (take, skip, _) =>
        {
            asked.Add((take, skip));
            return Task.FromResult<IReadOnlyList<int>>([.. Enumerable.Range(0, Math.Min(available, take))]);
        };
}
