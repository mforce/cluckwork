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
