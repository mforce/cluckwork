namespace Cluckwork.Api.IntegrationTests.Infrastructure;

using Google.Protobuf;

public sealed class OtlpPayloadAssertionsTests
{
    [Fact]
    public void Expected_trace_payload_requires_the_trace_under_the_cluckwork_resource()
    {
        const string traceId = "0102030405060708090a0b0c0d0e0f10";

        Assert.True(OtlpPayloadAssertions.IsExpectedTracePayload(TracePayload("Cluckwork.Api", traceId), traceId));
        Assert.False(OtlpPayloadAssertions.IsExpectedTracePayload(SplitTraceResourcePayload(traceId), traceId));
    }

    [Fact]
    public void Expected_metric_payload_requires_the_login_route_and_complete_resource_scope_hierarchy()
    {
        Assert.True(OtlpPayloadAssertions.HasExpectedMetricPayload(CompletePayload("/api/v1/auth/login")));
        Assert.False(OtlpPayloadAssertions.HasExpectedMetricPayload(SplitResourcePayload()));
        Assert.False(OtlpPayloadAssertions.HasExpectedMetricPayload(SplitScopePayload()));
        Assert.False(OtlpPayloadAssertions.HasExpectedMetricPayload(EmptyScopePayload()));
        Assert.False(OtlpPayloadAssertions.HasExpectedMetricPayload(CompletePayload("/health/ready")));
    }

    private static byte[] CompletePayload(string route) => Request(ResourceMetrics(
        Resource("Cluckwork.Api"),
        ScopeMetrics("Microsoft.AspNetCore.Hosting", Metric("http.server.request.duration", Histogram(DataPoint(route)))),
        ScopeMetrics("Npgsql", Metric("db.client.operation.duration")),
        ScopeMetrics("Microsoft.EntityFrameworkCore", Metric("microsoft.entityframeworkcore.queries")),
        ScopeMetrics("System.Runtime", Metric("dotnet.gc.collections"))));

    private static byte[] SplitResourcePayload() => Request(
        ResourceMetrics(
            Resource("Cluckwork.Api"),
            ScopeMetrics("Microsoft.AspNetCore.Hosting", Metric("http.server.request.duration", Histogram(DataPoint("/api/v1/auth/login"))))),
        ResourceMetrics(
            Resource("other-service"),
            ScopeMetrics("Npgsql", Metric("db.client.operation.duration")),
            ScopeMetrics("Microsoft.EntityFrameworkCore", Metric("microsoft.entityframeworkcore.queries")),
            ScopeMetrics("System.Runtime", Metric("dotnet.gc.collections"))));

    private static byte[] SplitScopePayload() => Request(ResourceMetrics(
        Resource("Cluckwork.Api"),
        ScopeMetrics("Microsoft.AspNetCore.Hosting",
            Metric("http.server.request.duration", Histogram(DataPoint("/api/v1/auth/login"))),
            Metric("db.client.operation.duration")),
        ScopeMetrics("Npgsql", Metric("microsoft.entityframeworkcore.queries")),
        ScopeMetrics("Microsoft.EntityFrameworkCore", Metric("dotnet.gc.collections")),
        ScopeMetrics("System.Runtime")));

    private static byte[] EmptyScopePayload() => Request(ResourceMetrics(
        Resource("Cluckwork.Api"),
        ScopeMetrics("",
            Metric("http.server.request.duration", Histogram(DataPoint("/api/v1/auth/login"))),
            Metric("db.client.operation.duration"),
            Metric("microsoft.entityframeworkcore.queries"),
            Metric("dotnet.gc.collections"))));

    private static byte[] TracePayload(string serviceName, string traceId) => TraceRequest(ResourceSpans(
        Resource(serviceName), ScopeSpans(Span(traceId))));

    private static byte[] SplitTraceResourcePayload(string traceId) => TraceRequest(
        ResourceSpans(Resource("Cluckwork.Api")),
        ResourceSpans(Resource("other-service"), ScopeSpans(Span(traceId))));

    private static byte[] TraceRequest(params byte[][] resourceSpans) => Message(output =>
    {
        foreach (var resourceSpan in resourceSpans) WriteMessage(output, 1, resourceSpan);
    });

    private static byte[] ResourceSpans(byte[] resource, params byte[][] scopeSpans) => Message(output =>
    {
        WriteMessage(output, 1, resource);
        foreach (var scopeSpan in scopeSpans) WriteMessage(output, 2, scopeSpan);
    });

    private static byte[] ScopeSpans(params byte[][] spans) => Message(output =>
    {
        foreach (var span in spans) WriteMessage(output, 2, span);
    });

    private static byte[] Span(string traceId) => Message(output =>
    {
        WriteMessage(output, 1, Convert.FromHexString(traceId));
        WriteString(output, 5, "POST /api/v1/auth/login");
        WriteVarint(output, 6, 2);
    });

    private static byte[] Request(params byte[][] resourceMetrics) => Message(output =>
    {
        foreach (var resourceMetric in resourceMetrics) WriteMessage(output, 1, resourceMetric);
    });

    private static byte[] ResourceMetrics(byte[] resource, params byte[][] scopeMetrics) => Message(output =>
    {
        WriteMessage(output, 1, resource);
        foreach (var scopeMetric in scopeMetrics) WriteMessage(output, 2, scopeMetric);
    });

    private static byte[] Resource(string serviceName) => Message(output =>
        WriteMessage(output, 1, Attribute("service.name", serviceName)));

    private static byte[] ScopeMetrics(string scopeName, params byte[][] metrics) => Message(output =>
    {
        WriteMessage(output, 1, Scope(scopeName));
        foreach (var metric in metrics) WriteMessage(output, 2, metric);
    });

    private static byte[] Scope(string name) => Message(output => WriteString(output, 1, name));

    private static byte[] Metric(string name, byte[]? histogram = null) => Message(output =>
    {
        WriteString(output, 1, name);
        if (histogram is not null) WriteMessage(output, 9, histogram);
    });

    private static byte[] Histogram(params byte[][] dataPoints) => Message(output =>
    {
        foreach (var dataPoint in dataPoints) WriteMessage(output, 1, dataPoint);
    });

    private static byte[] DataPoint(string route) => Message(output =>
        WriteMessage(output, 9, Attribute("http.route", route)));

    private static byte[] Attribute(string key, string value) => Message(output =>
    {
        WriteString(output, 1, key);
        WriteMessage(output, 2, Message(anyValue => WriteString(anyValue, 1, value)));
    });

    private static byte[] Message(Action<CodedOutputStream> write)
    {
        using var stream = new MemoryStream();
        using (var output = new CodedOutputStream(stream, leaveOpen: true))
        {
            write(output);
            output.Flush();
        }
        return stream.ToArray();
    }

    private static void WriteMessage(CodedOutputStream output, int field, byte[] value)
    {
        output.WriteTag(WireFormat.MakeTag(field, WireFormat.WireType.LengthDelimited));
        output.WriteBytes(ByteString.CopyFrom(value));
    }

    private static void WriteString(CodedOutputStream output, int field, string value)
    {
        output.WriteTag(WireFormat.MakeTag(field, WireFormat.WireType.LengthDelimited));
        output.WriteString(value);
    }

    private static void WriteVarint(CodedOutputStream output, int field, ulong value)
    {
        output.WriteTag(WireFormat.MakeTag(field, WireFormat.WireType.Varint));
        output.WriteUInt64(value);
    }
}
