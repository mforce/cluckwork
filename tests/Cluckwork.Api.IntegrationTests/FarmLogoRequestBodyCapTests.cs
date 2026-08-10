namespace Cluckwork.Api.IntegrationTests;

using System.Text.Json;
using Cluckwork.Api.Configuration;
using Cluckwork.Api.Endpoints.Accounts;
using Cluckwork.Api.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// #442 review (codex) — KestrelRequestBodyLimitTests' Kestrel-backed tests
// prove the observable RESPONSE SHAPE for an oversized logo upload, but
// proving WHEN the cap fires relative to "whatever runs next" (Idempotency-
// Middleware's own hash-read, in production) over a REAL socket depends on
// Kestrel's internal pipe buffering and the OS's TCP receive buffer — neither
// controlled by this repo, both easily able to absorb well past a modest
// multiple of the cap before a client's send genuinely stalls, regardless of
// whether the ordering fix is even in place. See the comment on
// KestrelRequestBodyLimitTests where an earlier, TCP-based version of this
// test lived and why it was replaced with this one.
//
// This proves the same property deterministically instead: a two-stage
// pipeline (FarmLogoRequestBodyCap, then a fake terminal delegate standing in
// for IdempotencyMiddleware's own unbounded CopyToAsync hash-read) over a
// plain in-memory, non-seekable stream — no networking, no OS/transport
// buffering, so the byte count observed is exact, not environment-dependent.
public sealed class FarmLogoRequestBodyCapTests
{
    private const int MaxBytes = 1024;

    private static RequestDelegate BuildPipeline(RequestDelegate terminal)
    {
        var app = new ApplicationBuilder(EmptyServiceProvider());
        app.UseFarmLogoRequestBodyCap();
        app.Run(terminal);
        return app.Build();
    }

    private static IServiceProvider EmptyServiceProvider() => new ServiceCollection().BuildServiceProvider();

    private static HttpContext ContextFor(Stream body, long? contentLength, bool carriesCapMetadata = true)
    {
        var services = new ServiceCollection();
        // MaxUploadBytes is `init`, so it can't be set via Configure's mutating
        // delegate — a fixed IOptionsSnapshot is simplest for a test double.
        services.AddSingleton<IOptionsSnapshot<FarmLogoOptions>>(
            new FixedOptionsSnapshot<FarmLogoOptions>(new FarmLogoOptions { MaxUploadBytes = MaxBytes }));
        // Results.Problem's ExecuteAsync resolves ILoggerFactory from
        // RequestServices — a bare ServiceCollection doesn't have one.
        services.AddLogging();

        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
        };
        context.Request.Body = body;
        context.Request.ContentLength = contentLength;
        context.Response.Body = new MemoryStream();

        var metadata = carriesCapMetadata
            ? new EndpointMetadataCollection(new FarmLogoUploadCapMetadata())
            : EndpointMetadataCollection.Empty;
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, metadata, "test"));

        return context;
    }

    private static async Task<JsonElement> ReadResponseJsonAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task A_declared_oversize_is_refused_before_the_downstream_delegate_runs()
    {
        var downstreamCalled = false;
        var pipeline = BuildPipeline(_ =>
        {
            downstreamCalled = true;
            return Task.CompletedTask;
        });
        var context = ContextFor(new MemoryStream(), contentLength: MaxBytes + 1);

        await pipeline(context);

        Assert.False(downstreamCalled, "the declared-oversize short-circuit must return before next() runs.");
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        var problem = await ReadResponseJsonAsync(context);
        Assert.Equal("FarmLogo.TooLarge", problem.GetProperty("title").GetString());
    }

    // The actual #442 regression test: an undeclared-length body (Content-
    // Length null, the chunked-transfer shape) that vastly exceeds the cap.
    // The downstream delegate mirrors IdempotencyMiddleware.
    // ComputeRequestHashAsync — an unbounded CopyToAsync with no cap of its
    // own — so the ONLY thing that can stop it short of the full 500x source
    // is FarmLogoRequestBodyCap's wrapped stream having already thrown.
    [Fact]
    public async Task An_undeclared_oversize_body_is_capped_during_the_downstream_read_not_after_it()
    {
        var source = new CountingReadStream(MaxBytes * 500L);
        var downstreamBytesSeen = -1L;
        var pipeline = BuildPipeline(async ctx =>
        {
            using var sink = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(sink);
            // Unreachable if the cap is doing its job — CopyToAsync throws
            // before returning. Left in so a regression fails LOUD (an
            // explicit wrong-value assertion) rather than by coincidentally
            // never running this line.
            downstreamBytesSeen = sink.Length;
        });
        var context = ContextFor(source, contentLength: null);

        await pipeline(context);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        var problem = await ReadResponseJsonAsync(context);
        Assert.Equal("FarmLogo.TooLarge", problem.GetProperty("title").GetString());
        Assert.Equal(-1L, downstreamBytesSeen);
        // The regression this guards: without FarmLogoRequestBodyCap wrapping
        // the body BEFORE the downstream delegate runs, nothing bounds this
        // read and source.BytesRead would reach the full 500x MaxBytes.
        Assert.True(source.BytesRead <= MaxBytes + 1,
            $"expected at most {MaxBytes + 1} bytes read from the source (it holds {MaxBytes * 500L}), " +
            $"but {source.BytesRead} were read — the downstream delegate consumed far more than the " +
            "cap allows, meaning the body was not capped before it ran.");
    }

    [Fact]
    public async Task A_body_within_the_cap_reaches_the_downstream_delegate_intact()
    {
        var payload = new byte[MaxBytes];
        Random.Shared.NextBytes(payload);
        byte[]? seenByDownstream = null;
        var pipeline = BuildPipeline(async ctx =>
        {
            using var sink = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(sink);
            seenByDownstream = sink.ToArray();
        });
        var context = ContextFor(new MemoryStream(payload), contentLength: null);

        await pipeline(context);

        Assert.Equal(payload, seenByDownstream);
        // The pipeline never wrote a response — default DefaultHttpContext status.
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task A_route_without_the_marker_metadata_is_left_untouched()
    {
        var downstreamCalled = false;
        var pipeline = BuildPipeline(_ =>
        {
            downstreamCalled = true;
            return Task.CompletedTask;
        });
        // Declared oversize AND no metadata: proves the middleware isn't
        // capping every request, only the one route it's meant for.
        var context = ContextFor(new MemoryStream(new byte[1]), contentLength: MaxBytes + 1, carriesCapMetadata: false);

        await pipeline(context);

        Assert.True(downstreamCalled, "a route without FarmLogoUploadCapMetadata must not be capped.");
    }

    // Mirrors ByteCappedRequestStream's own non-seekable, count-as-you-go
    // shape (RequestBodyLimit.cs), but exposes BytesRead for assertion.
    private sealed class CountingReadStream(long length) : Stream
    {
        private long _pos;
        public long BytesRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, length - _pos);
            if (n <= 0) return 0;
            Array.Fill(buffer, (byte)0xAA, offset, n);
            _pos += n;
            BytesRead += n;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer, offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = (int)Math.Min(buffer.Length, length - _pos);
            if (n <= 0) return ValueTask.FromResult(0);
            buffer.Span[..n].Fill((byte)0xAA);
            _pos += n;
            BytesRead += n;
            return ValueTask.FromResult(n);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FixedOptionsSnapshot<T>(T value) : IOptionsSnapshot<T> where T : class
    {
        public T Value => value;
        public T Get(string? name) => value;
    }
}
