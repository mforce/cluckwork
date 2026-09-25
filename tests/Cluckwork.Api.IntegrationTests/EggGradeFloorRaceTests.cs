namespace Cluckwork.Api.IntegrationTests;

using System.Data.Common;
using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

public sealed class EggGradeFloorRaceFactory : CluckworkWebApplicationFactory
{
    public GradeReadRendezvousInterceptor Rendezvous { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
            services.AddDbContext<AppDbContext>((_, options) => options.AddInterceptors(Rendezvous)));
    }
}

// Holds the first N readers of an EggGrade at their SELECT until all N have
// arrived, then releases them together. Each request reads the grade once
// before it writes, so no single request can satisfy the count alone: the
// rendezvous is what makes "both writers saw the same Version" a fact rather
// than a hope about thread timing.
public sealed class GradeReadRendezvousInterceptor : DbCommandInterceptor
{
    private readonly Lock gate = new();
    private TaskCompletionSource released = NewSignal();
    private int expected;
    private int arrived;

    public void ArmFor(int readers)
    {
        lock (gate)
        {
            expected = readers;
            arrived = 0;
            released = NewSignal();
        }
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        TaskCompletionSource? wait = null;
        lock (gate)
        {
            if (expected > 0 && command.CommandText.Contains("FROM \"EggGrades\"", StringComparison.Ordinal))
            {
                wait = released;
                if (++arrived >= expected)
                {
                    expected = 0;
                    released.TrySetResult();
                }
            }
        }

        if (wait is not null) await wait.Task.WaitAsync(cancellationToken);
        return result;
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

// #950 review round 1 (Codex gpt-6-sol): the floor's parallel-update test used
// to accept two successes, so it would have stayed green with the Version
// concurrency token removed and the two requests merely serialized. This one
// forces both writers to read the same Version first, which makes exactly one
// 204 and one 409 the only correct outcome.
public sealed class EggGradeFloorRaceTests(EggGradeFloorRaceFactory factory)
    : IClassFixture<EggGradeFloorRaceFactory>
{
    private sealed record IdDto(Guid Id);
    private sealed record GradeDto(Guid Id, string Name, int? LowStockFloor);

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, Guid id, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/egg-grades/{id}")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return client.SendAsync(request);
    }

    [Fact]
    public async Task Two_floor_updates_off_one_version_leave_exactly_one_winner()
    {
        var email = $"race-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        var create = await client.PostWithKeyAsync(
            "/api/v1/egg-grades", Guid.NewGuid().ToString(),
            new { name = $"Race {Guid.NewGuid():N}", gradeType = "Size", sortOrder = 0, isSaleable = true });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var id = (await create.Content.ReadFromJsonAsync<IdDto>())!.Id;

        factory.Rendezvous.ArmFor(2);
        var responses = await Task.WhenAll(
            PutAsync(client, id, new { name = "Race", sortOrder = 0, isSaleable = true, lowStockFloor = 1000 }),
            PutAsync(client, id, new { name = "Race", sortOrder = 0, isSaleable = true, lowStockFloor = 2000 }));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.NoContent));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        var grade = (await client.GetFromJsonAsync<List<GradeDto>>("/api/v1/egg-grades"))!.Single(g => g.Id == id);
        Assert.Contains(grade.LowStockFloor, new int?[] { 1000, 2000 });

        // One winner means one committed Version bump and one audit row: the
        // loser's write never reached the database at all.
        var version = await factory.WithTenantScopeAsync(accountId, async db =>
            (await db.EggGrades.AsNoTracking().FirstAsync(g => g.Id == id)).Version);
        Assert.Equal(1, version);

        var updates = await factory.WithTenantScopeAsync(accountId, db => db.AuditEvents
            .Where(e => e.EntityType == "EggGrade" && e.EntityId == id && e.Action == "EggGrade.Update")
            .CountAsync());
        Assert.Equal(1, updates);
    }
}
