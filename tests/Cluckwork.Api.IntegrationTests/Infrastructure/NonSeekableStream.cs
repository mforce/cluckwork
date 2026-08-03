namespace Cluckwork.Api.IntegrationTests.Infrastructure;

// A read stream that can't report its own Length (CanSeek = false), so it pairs
// with an explicit/lying Content-Length override to simulate an undeclared-length
// body under TestServer.
//
// This is what reaches RequestBodyLimit's LAYER-3 read cap
// (ByteCappedRequestStream), which throws its 413 mid-binding — as opposed to the
// declared-length short-circuit, which refuses before the request ever reaches the
// endpoint. Both produce a 413 to the client, so a test that accidentally takes
// the declared-length path still passes while proving nothing about the streamed
// one. Using this stream is what keeps that distinction real.
//
// Shared (#398 review round 2, Codex): AuthBodyLimitTests pins the streamed 413's
// RESPONSE shape and RequestLoggingTests pins its TELEMETRY. Both need the same
// transport shape, and two copies could drift apart — at which point one of them
// would silently stop exercising the path it names.
internal sealed class NonSeekableStream(byte[] data) : Stream
{
    private int _pos;

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = Math.Min(count, data.Length - _pos);
        if (n <= 0) return 0;
        Array.Copy(data, _pos, buffer, offset, n);
        _pos += n;
        return n;
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
