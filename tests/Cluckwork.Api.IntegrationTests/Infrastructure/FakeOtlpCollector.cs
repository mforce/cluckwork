namespace Cluckwork.Api.IntegrationTests.Infrastructure;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

internal sealed record CapturedOtlpRequest(
    string Path,
    byte[] Body,
    IReadOnlyDictionary<string, string> Headers);

// Minimal HTTP/protobuf OTLP sink. Trace and metric exports can arrive in either
// order, and startup batches can precede a causally asserted request, so each
// path has a FIFO queue rather than a one-shot completion source.
internal sealed class FakeOtlpCollector : IDisposable
{
    private readonly HttpListener _listener;
    private readonly ConcurrentDictionary<string, Channel<CapturedOtlpRequest>> _byPath = new();
    private readonly object _stateGate = new();
    private readonly TaskCompletionSource<Exception> _terminal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<CapturedOtlpRequest> _anyRequest =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _serveTask;
    private Exception? _terminalException;
    private int _publishedRequestCount;

    public FakeOtlpCollector() : this(FreePort)
    {
    }

    // The port source is injectable so a test can hand the first attempt a port that is
    // already taken; every other caller in the suite goes through the parameterless constructor.
    internal FakeOtlpCollector(Func<int> portSource)
    {
        ArgumentNullException.ThrowIfNull(portSource);

        for (var attempt = 1; ; attempt++)
        {
            var port = portSource();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                _listener = listener;
                Endpoint = $"http://127.0.0.1:{port}";
                break;
            }
            catch (HttpListenerException)
            {
                // Start() closes the listener on ANY failure, so the next attempt needs a
                // fresh instance as well as a fresh port: reusing this one throws
                // ObjectDisposedException from the very next Prefixes access.
                ((IDisposable)listener).Dispose();
                if (attempt >= BindAttempts) throw;
            }
        }

        _serveTask = Task.Run(ServeAsync);
    }

    private const int BindAttempts = 10;

    public string Endpoint { get; }

    internal Action? OnTimeoutCaughtForTest { get; set; }

    internal Action? BeforeResponseCloseForTest { get; set; }

    internal int PublishedRequestCountForTest => Volatile.Read(ref _publishedRequestCount);

    public async Task<byte[]> WaitForPathAsync(string path, TimeSpan timeout) =>
        (await WaitForRequestAsync(path, timeout)).Body;

    public async Task<CapturedOtlpRequest> WaitForRequestAsync(string path, TimeSpan timeout)
    {
        return await WaitForRequestAsync(path, static _ => true, timeout);
    }

    public async Task<CapturedOtlpRequest> WaitForRequestAsync(
        string path,
        Func<CapturedOtlpRequest, bool> predicate,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        using var timeoutCancellation = new CancellationTokenSource(timeout);
        var reader = For(path).Reader;
        try
        {
            while (true)
            {
                ThrowIfTerminated();
                var request = reader.ReadAsync(timeoutCancellation.Token).AsTask();
                var winner = await Task.WhenAny(_terminal.Task, request);
                if (winner == _terminal.Task) ThrowIfTerminated();

                CapturedOtlpRequest captured;
                try { captured = await request; }
                catch (ChannelClosedException)
                {
                    ThrowIfTerminated();
                    throw;
                }
                ThrowIfTerminated();
                if (predicate(captured)) return captured;
            }
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
        {
            OnTimeoutCaughtForTest?.Invoke();
            ThrowIfTerminated();
            throw new TimeoutException($"no matching OTLP export arrived on {path} before the timeout");
        }
    }

    public async Task AssertNoRequestAsync(TimeSpan observationWindow)
    {
        ThrowIfTerminated();
        var completed = await Task.WhenAny(_terminal.Task, _anyRequest.Task, Task.Delay(observationWindow));
        ThrowIfTerminated();
        Assert.True(completed != _anyRequest.Task,
            "an OTLP request arrived while export was expected to be disabled");
    }

    internal void FaultForTest(Exception exception) => Fault(exception);

    private void ThrowIfTerminated()
    {
        if (_terminal.Task.IsCompleted)
            throw _terminal.Task.GetAwaiter().GetResult();
    }

    private Channel<CapturedOtlpRequest> For(string path)
    {
        var channel = _byPath.GetOrAdd(path, _ => Channel.CreateUnbounded<CapturedOtlpRequest>(
            new UnboundedChannelOptions { SingleWriter = true }));
        lock (_stateGate)
            if (_terminalException is { } exception)
                channel.Writer.TryComplete(exception);
        return channel;
    }

    private void Fault(Exception exception)
    {
        lock (_stateGate)
        {
            if (_terminalException is not null) return;

            _terminalException = exception;
            _terminal.TrySetResult(exception);
            foreach (var channel in _byPath.Values)
                channel.Writer.TryComplete(exception);
        }
    }

    private void Publish(CapturedOtlpRequest captured)
    {
        lock (_stateGate)
        {
            if (_terminalException is { } exception) throw exception;
            if (!For(captured.Path).Writer.TryWrite(captured))
                throw new InvalidOperationException($"OTLP request queue for {captured.Path} was closed");
            _anyRequest.TrySetResult(captured);
            Interlocked.Increment(ref _publishedRequestCount);
        }
    }

    private async Task ServeAsync()
    {
        HttpListenerContext? currentContext = null;
        try
        {
            while (_listener.IsListening)
            {
                try
                {
                    currentContext = await _listener.GetContextAsync();

                    // Anything that is not an OTLP export is answered and dropped: a local port
                    // scanner's GET / must not read as "the child exported while export was disabled".
                    if (!IsOtlpExport(currentContext.Request))
                    {
                        currentContext.Response.StatusCode = 404;
                        currentContext.Response.Close();
                        currentContext = null;
                        continue;
                    }

                    using var buffer = new MemoryStream();
                    await currentContext.Request.InputStream.CopyToAsync(buffer);
                    var headers = currentContext.Request.Headers.AllKeys
                        .Where(key => key is not null)
                        .ToDictionary(key => key!, key => currentContext.Request.Headers[key!] ?? string.Empty,
                            StringComparer.OrdinalIgnoreCase);
                    var captured = new CapturedOtlpRequest(currentContext.Request.Url!.AbsolutePath, buffer.ToArray(), headers);
                    currentContext.Response.StatusCode = 200;
                    BeforeResponseCloseForTest?.Invoke();
                    currentContext.Response.Close();
                    Publish(captured);
                    currentContext = null;
                }
                catch (Exception ex) when (ex is HttpListenerException or IOException && _listener.IsListening)
                {
                    // An unrelated client died mid-exchange. That is not this collector's business,
                    // and faulting here would redden whatever test happens to hold it.
                    try { currentContext?.Response.Abort(); } catch { /* already gone */ }
                    currentContext = null;
                }
            }
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException && !_listener.IsListening)
        {
            // Normal disposal interrupts a pending accept.
        }
        catch (Exception ex)
        {
            try { currentContext?.Response.Abort(); } catch { /* already closed */ }
            Fault(ex);
        }
    }

    private static bool IsOtlpExport(HttpListenerRequest request) =>
        string.Equals(request.HttpMethod, "POST", StringComparison.Ordinal)
        && request.Url is { AbsolutePath: var path }
        && path.StartsWith("/v1/", StringComparison.Ordinal);

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    public void Dispose()
    {
        Fault(new ObjectDisposedException(nameof(FakeOtlpCollector)));
        try { _listener.Stop(); } catch { /* already stopped */ }
        ((IDisposable)_listener).Dispose();
        _serveTask.GetAwaiter().GetResult();
    }
}
