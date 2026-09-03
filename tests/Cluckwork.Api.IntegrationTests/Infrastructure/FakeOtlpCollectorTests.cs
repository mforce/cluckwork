namespace Cluckwork.Api.IntegrationTests.Infrastructure;

using System.Net;
using System.Net.Sockets;

public sealed class FakeOtlpCollectorTests
{
    [Fact]
    public async Task Bind_retries_with_a_fresh_listener_after_a_lost_port_race()
    {
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        var takenPort = ((IPEndPoint)occupied.LocalEndpoint).Port;
        var answers = new Queue<int>([takenPort]);

        using var collector = new FakeOtlpCollector(() => answers.Count > 0 ? answers.Dequeue() : FreeTestPort());
        using var client = new HttpClient();

        var post = client.PostAsync($"{collector.Endpoint}/v1/traces", new ByteArrayContent([0x01]));
        var captured = await collector.WaitForRequestAsync("/v1/traces", TimeSpan.FromSeconds(5));

        Assert.Equal("/v1/traces", captured.Path);
        Assert.DoesNotContain($":{takenPort}", collector.Endpoint, StringComparison.Ordinal);
        (await post).Dispose();
    }

    [Fact]
    public async Task Traffic_that_is_not_an_otlp_export_is_answered_and_never_published()
    {
        using var collector = new FakeOtlpCollector();
        using var client = new HttpClient();

        var scan = await client.GetAsync($"{collector.Endpoint}/");
        var wrongPath = await client.PostAsync($"{collector.Endpoint}/", new ByteArrayContent([0x01]));
        var wrongMethod = await client.GetAsync($"{collector.Endpoint}/v1/traces");

        // The captured symptom of #676 comes first, so this test reddens on the bug's own message.
        await collector.AssertNoRequestAsync(TimeSpan.FromMilliseconds(200));
        Assert.Equal(0, collector.PublishedRequestCountForTest);
        Assert.Equal(HttpStatusCode.NotFound, scan.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, wrongPath.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, wrongMethod.StatusCode);

        var post = client.PostAsync($"{collector.Endpoint}/v1/traces", new ByteArrayContent([0x02]));
        var captured = await collector.WaitForRequestAsync("/v1/traces", TimeSpan.FromSeconds(5));
        Assert.Equal(new byte[] { 0x02 }, captured.Body);
        (await post).Dispose();
        scan.Dispose();
        wrongPath.Dispose();
        wrongMethod.Dispose();
    }

    [Fact]
    public async Task A_client_that_dies_mid_request_does_not_fault_the_collector()
    {
        using var collector = new FakeOtlpCollector();
        var endpoint = new Uri(collector.Endpoint);

        using (var rude = new TcpClient())
        {
            rude.Connect(IPAddress.Loopback, endpoint.Port);
            rude.LingerState = new LingerOption(true, 0);
            var truncated = "POST /v1/traces HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Length: 100\r\n\r\nxx"u8.ToArray();
            rude.GetStream().Write(truncated);
            rude.GetStream().Flush();
        }

        using var client = new HttpClient();
        var post = client.PostAsync($"{collector.Endpoint}/v1/traces", new ByteArrayContent([0x03]));
        var captured = await collector.WaitForRequestAsync("/v1/traces", TimeSpan.FromSeconds(5));

        Assert.Equal(new byte[] { 0x03 }, captured.Body);
        (await post).Dispose();
    }

    [Fact]
    public async Task An_export_that_dies_mid_transfer_is_reported_not_silently_dropped()
    {
        using var collector = new FakeOtlpCollector();
        var endpoint = new Uri(collector.Endpoint);

        using (var rude = new TcpClient())
        {
            rude.Connect(IPAddress.Loopback, endpoint.Port);
            rude.LingerState = new LingerOption(true, 0);
            var truncated = "POST /v1/traces HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Length: 100\r\n\r\nxx"u8.ToArray();
            rude.GetStream().Write(truncated);
            rude.GetStream().Flush();
        }

        // The collector must not fault (that is the dead-client rule), but it must not pretend the
        // export never arrived either: an export-shaped request that died mid-body is still one that
        // arrived, and "no export arrived" has to fail.
        var failure = await Assert.ThrowsAnyAsync<Exception>(
            () => collector.AssertNoRequestAsync(TimeSpan.FromSeconds(1)));

        Assert.Contains("its body never completed", failure.Message, StringComparison.Ordinal);
        Assert.Equal(1, collector.AbortedExportCountForTest);
        Assert.Equal(0, collector.PublishedRequestCountForTest);
    }

    private static int FreeTestPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    [Fact]
    public async Task Predicate_wait_throws_a_terminal_error_completed_at_the_timeout_catch_boundary()
    {
        using var collector = new FakeOtlpCollector();
        var fault = new InvalidOperationException("terminal fault at timeout boundary");
        collector.OnTimeoutCaughtForTest = () => collector.FaultForTest(fault);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => collector.WaitForRequestAsync(
            "/v1/traces", static _ => false, TimeSpan.FromMilliseconds(25)));

        Assert.Same(fault, exception);
    }

    [Fact]
    public async Task Response_completion_failure_faults_without_publishing_the_request()
    {
        using var collector = new FakeOtlpCollector();
        using var client = new HttpClient();
        var fault = new InvalidOperationException("response completion failed");
        collector.BeforeResponseCloseForTest = () => throw fault;

        var post = client.PostAsync($"{collector.Endpoint}/v1/traces", new ByteArrayContent(new byte[] { 0x01 }));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            collector.WaitForRequestAsync("/v1/traces", TimeSpan.FromSeconds(1)));

        Assert.Same(fault, exception);
        Assert.Equal(0, collector.PublishedRequestCountForTest);
        try { await post; } catch (HttpRequestException) { /* response completion fault is expected */ }
    }

    [Fact]
    public async Task Predicate_wait_times_out_when_no_queued_request_matches()
    {
        using var collector = new FakeOtlpCollector();

        var exception = await Assert.ThrowsAsync<TimeoutException>(() => collector.WaitForRequestAsync(
            "/v1/traces", static _ => false, TimeSpan.FromMilliseconds(50)));

        Assert.Contains("no matching OTLP export", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Predicate_wait_prioritizes_an_already_completed_collector_fault_over_a_queued_request()
    {
        using var collector = new FakeOtlpCollector();
        using var client = new HttpClient();
        var fault = new InvalidOperationException("injected collector fault");

        await PostAsync(client, collector.Endpoint, "/v1/traces", new byte[] { 0x01 });
        collector.FaultForTest(fault);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            collector.WaitForRequestAsync("/v1/traces", TimeSpan.FromSeconds(1)));

        Assert.Same(fault, exception);
    }

    [Fact]
    public async Task Dispose_wakes_a_pending_predicate_wait_with_an_intentional_disposal_exception()
    {
        var collector = new FakeOtlpCollector();
        var pending = collector.WaitForRequestAsync("/v1/traces", static _ => true, TimeSpan.FromSeconds(5));

        collector.Dispose();

        var completed = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.Same(pending, completed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pending);
    }

    [Fact]
    public async Task Predicate_wait_retains_later_request_on_the_same_path_after_skipping_an_earlier_batch()
    {
        using var collector = new FakeOtlpCollector();
        using var client = new HttpClient();
        var startupBatch = new byte[] { 0x01 };
        var causalBatch = new byte[] { 0x02 };
        var inspectedBatches = new List<byte[]>();

        await PostAsync(client, collector.Endpoint, "/v1/traces", startupBatch);
        await PostAsync(client, collector.Endpoint, "/v1/traces", causalBatch);

        var captured = await collector.WaitForRequestAsync(
            "/v1/traces",
            request =>
            {
                inspectedBatches.Add(request.Body);
                return request.Body.SequenceEqual(causalBatch);
            },
            TimeSpan.FromSeconds(2));

        Assert.Collection(inspectedBatches,
            body => Assert.Equal(startupBatch, body),
            body => Assert.Equal(causalBatch, body));
        Assert.Equal(causalBatch, captured.Body);
    }

    private static async Task PostAsync(HttpClient client, string endpoint, string path, byte[] body)
    {
        using var response = await client.PostAsync($"{endpoint}{path}", new ByteArrayContent(body));
        response.EnsureSuccessStatusCode();
    }
}
