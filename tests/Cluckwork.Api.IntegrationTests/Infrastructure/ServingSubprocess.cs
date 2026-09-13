namespace Cluckwork.Api.IntegrationTests.Infrastructure;

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

// A real Cluckwork.Api.dll child on its own ephemeral loopback port, with both pipes drained for its
// whole life (an unread pipe fills its OS buffer and the child blocks on write) and kept, so a child
// that dies before readiness can say why.
internal sealed class ServingSubprocess : IAsyncDisposable
{
    // The port comes from a closed probe, so it is unheld for the whole of Process.Start plus the
    // child's startup and anything in this socket-busy test process can be handed it meanwhile.
    // The bind failure is only observable once the child has exited, inside the readiness wait, so
    // the retry spans spawn-and-wait-for-ready. Same shape and cap as FakeOtlpCollector's listener.
    private const int BindAttempts = 10;

    private readonly Process _process;
    private readonly Task<string> _stdout;
    private readonly Task<string> _stderr;

    private ServingSubprocess(Process process, Uri baseUrl)
    {
        _process = process;
        _stdout = process.StandardOutput.ReadToEndAsync();
        _stderr = process.StandardError.ReadToEndAsync();
        BaseUrl = baseUrl;
    }

    public Uri BaseUrl { get; }

    public static ServingSubprocess Start(ProcessStartInfo startInfo, int port)
    {
        startInfo.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        return new ServingSubprocess(Process.Start(startInfo)!, new Uri($"http://127.0.0.1:{port}"));
    }

    public static Task<ServingSubprocess> StartReadyAsync(ProcessStartInfo startInfo, TimeSpan readyTimeout) =>
        StartReadyAsync(FreeTcpPort, startInfo, readyTimeout);

    // The port source is injectable so a test can hand the first attempt a port that is already
    // taken; every other caller goes through the overload above.
    internal static async Task<ServingSubprocess> StartReadyAsync(
        Func<int> portSource, ProcessStartInfo startInfo, TimeSpan readyTimeout)
    {
        for (var attempt = 1; ; attempt++)
        {
            var child = Start(startInfo, portSource());
            try
            {
                await child.WaitUntilReadyAsync(readyTimeout);
                return child;
            }
            catch (ChildExitedBeforeReadyException ex) when (ex.LostItsPort)
            {
                await child.DisposeAsync();
                if (attempt >= BindAttempts)
                    throw new InvalidOperationException(
                        $"child lost its port on all {BindAttempts} attempts. {ex.Message}", ex);
            }
        }
    }

    public static int FreeTcpPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    public async Task WaitUntilReadyAsync(TimeSpan timeout)
    {
        using var client = new HttpClient { BaseAddress = BaseUrl, Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow + timeout;
        Exception? lastError = null;
        while (DateTime.UtcNow < deadline)
        {
            if (_process.HasExited)
            {
                var (_, stdout, stderr) = await WaitForExitAsync(TimeSpan.Zero);
                throw new ChildExitedBeforeReadyException(stdout, stderr);
            }
            try
            {
                if ((await client.GetAsync("/health/ready")).IsSuccessStatusCode) return;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }
        throw new TimeoutException($"child at {BaseUrl} did not become ready within {timeout}: {lastError?.Message}");
    }

    public async Task<(int ExitCode, string Stdout, string Stderr)> WaitForExitAsync(TimeSpan timeout)
    {
        if (!_process.HasExited && timeout > TimeSpan.Zero)
        {
            var waitForExit = _process.WaitForExitAsync();
            var exited = await Task.WhenAny(waitForExit, Task.Delay(timeout));
            if (exited != waitForExit)
                throw new TimeoutException($"child did not exit within {timeout}");
        }
        return (_process.HasExited ? _process.ExitCode : -1, await _stdout, await _stderr);
    }

    public async Task<(int ExitCode, string Stdout, string Stderr)> StopAsync()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch { /* exited while stopping */ }
        await _process.WaitForExitAsync();
        return (_process.ExitCode, await _stdout, await _stderr);
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync(); } catch { /* process already disposed */ }
        _process.Dispose();
    }

    // Only a child whose own output names the bind failure is retried; any other death keeps the
    // stdout/stderr diagnostic several cases depend on and fails on the first attempt.
    internal sealed class ChildExitedBeforeReadyException(string stdout, string stderr)
        : InvalidOperationException($"child exited before readiness. stdout={stdout} stderr={stderr}")
    {
        public bool LostItsPort { get; } =
            (stdout + stderr).Contains("address already in use", StringComparison.OrdinalIgnoreCase);
    }
}
