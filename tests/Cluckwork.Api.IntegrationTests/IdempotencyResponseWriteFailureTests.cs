using System.Net;
using System.Net.Http.Headers;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cluckwork.Api.IntegrationTests;

// #809 — since IdempotentUnit, IdempotencyMiddleware writes the response after the unit has
// committed and returned. A failure writing that response no longer re-enters the unit's
// cleanup to emit the stored result a second time; it reaches the exception handler. What
// keeps the write exactly-once is the client's retry with the same key, which replays.
[Collection(IntegrationCollection.Name)]
public sealed class IdempotencyResponseWriteFailureTests(CluckworkWebApplicationFactory factory)
{
    private const string FailHeader = "X-Test-Fail-Response-Write";

    private sealed record RecordedDto(Guid Id);

    [Fact]
    public async Task ResponseWriteFailsAfterCommit_SameKeyRetry_ReplaysWithoutASecondWrite()
    {
        var failures = 0;
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IStartupFilter>(new FailFirstWrite(() => Interlocked.Increment(ref failures)))));
        var email = $"write-fail-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var farmId = Guid.NewGuid();
        var flockId = await factory.SeedFlockAsync(accountId, farmId);
        var client = host.CreateClient(TestHarness.Cookieless(factory));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await factory.LoginForAccessTokenAsync(email));
        var key = Guid.NewGuid().ToString();
        var body = new
        {
            farmId, houseId = Guid.NewGuid(), flockId, date = DateOnly.FromDateTime(DateTime.UtcNow.Date),
            totalEggs = 100, crackedEggs = 0, dirtyEggs = 0, discardedEggs = 0, mortalityCount = 0,
        };

        using var failing = new HttpRequestMessage(HttpMethod.Post, "/api/v1/daily-entries") { Content = JsonContent.Create(body) };
        failing.Headers.Add("Idempotency-Key", key);
        failing.Headers.Add(FailHeader, "1");
        var lost = await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(failing));
        var retry = await client.PostWithKeyAsync("/api/v1/daily-entries", key, body);

        Assert.Equal(1, failures);
        Assert.Contains("simulated client disconnect", lost.InnerException?.InnerException?.Message);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        var stored = await factory.WithTenantScopeAsync(accountId, db => db.DailyEntries.SingleAsync(e => e.FlockId == flockId));
        Assert.Equal(stored.Id, (await retry.Content.ReadFromJsonAsync<RecordedDto>())!.Id);
        var audit = await factory.WithTenantScopeAsync(accountId, db =>
            db.AuditEvents.Where(a => a.EntityId == stored.Id).Select(a => a.Action).ToListAsync());
        Assert.Equal([AuditActions.DailyEntryCreate], audit);
    }

    // Outermost middleware: on a marked request, the first write to the response body
    // throws, as a dropped client connection would.
    private sealed class FailFirstWrite(Action onFailure) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Headers.ContainsKey(FailHeader))
                    context.Response.Body = new FailOnceStream(context.Response.Body, onFailure);
                await nextMiddleware(context);
            });
            next(app);
        };
    }

    private sealed class FailOnceStream(Stream inner, Action onFailure) : Stream
    {
        private int _failed;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            FailOnce();
            inner.Write(buffer, offset, count);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            FailOnce();
            await inner.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private void FailOnce()
        {
            if (Interlocked.Exchange(ref _failed, 1) != 0) return;
            onFailure();
            throw new IOException("simulated client disconnect while writing the response");
        }
    }
}
