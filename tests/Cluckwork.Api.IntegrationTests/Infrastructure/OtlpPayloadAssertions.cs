namespace Cluckwork.Api.IntegrationTests.Infrastructure;

using System.Text;
using Google.Protobuf;

internal static class OtlpPayloadAssertions
{
    private const string ServiceName = "Cluckwork.Api";
    private const string HttpScope = "Microsoft.AspNetCore.Hosting";
    private const string NpgsqlScope = "Npgsql";
    private const string EntityFrameworkScope = "Microsoft.EntityFrameworkCore";
    private const string RuntimeScope = "System.Runtime";
    private const string LoginRoute = "/api/v1/auth/login";

    public static bool IsExpectedTracePayload(byte[] body, string expectedTraceId)
    {
        var payload = DecodeTracePayload(body);
        return HasExpectedTraceHierarchy(payload, expectedTraceId);
    }

    public static void AssertTracePayload(byte[] body, string expectedTraceId)
    {
        var payload = DecodeTracePayload(body);
        Assert.NotEmpty(payload.Resources);
        Assert.Contains(payload.Resources, resource => IsCluckworkServiceResource(resource.Resource));
        Assert.True(HasExpectedTraceHierarchy(payload, expectedTraceId));
    }

    public static bool HasExpectedMetricPayload(byte[] body)
    {
        var payload = DecodeMetricPayload(body);
        return payload.Resources.Any(HasExpectedMetricHierarchy);
    }

    public static void AssertMetricPayload(byte[] body)
    {
        var payload = DecodeMetricPayload(body);
        Assert.NotEmpty(payload.Resources);
        Assert.Contains(payload.Resources, resource => IsCluckworkServiceResource(resource.Resource));
        Assert.Contains(payload.Resources, HasExpectedMetricHierarchy);
    }

    private static bool HasExpectedMetricHierarchy(DecodedResourceMetrics resourceMetrics) =>
        IsCluckworkServiceResource(resourceMetrics.Resource)
        && HasMetric(resourceMetrics, HttpScope, "http.server.request.duration", HasLoginRoute)
        && HasMetric(resourceMetrics, NpgsqlScope, "db.client.operation.duration")
        && HasMetric(resourceMetrics, EntityFrameworkScope, "microsoft.entityframeworkcore.queries")
        && HasMetric(resourceMetrics, RuntimeScope, "dotnet.gc.collections");

    private static bool HasExpectedTraceHierarchy(DecodedTracePayload payload, string expectedTraceId) =>
        payload.Resources.Any(resource => IsCluckworkServiceResource(resource.Resource)
            && resource.Spans.Any(span => IsExpectedServerSpan(span, expectedTraceId)));

    private static bool HasMetric(
        DecodedResourceMetrics resourceMetrics,
        string scopeName,
        string metricName,
        Func<DecodedMetric, bool>? additionalPredicate = null) =>
        resourceMetrics.Scopes.Any(scope => scope.Name == scopeName
            && scope.Metrics.Any(metric => metric.Name == metricName
                && (additionalPredicate is null || additionalPredicate(metric))));

    // OTLP metric.oneof histogram = 9; Histogram.data_points = 1;
    // HistogramDataPoint.attributes = 9; KeyValue.key = 1/value = 2;
    // AnyValue.string_value = 1.
    private static bool HasLoginRoute(DecodedMetric metric) =>
        Fields(metric.Encoded, 9)
            .SelectMany(histogram => Fields(histogram, 1))
            .Any(dataPoint => Fields(dataPoint, 9)
                .Any(attribute => Text(attribute, 1) == "http.route"
                    && Fields(attribute, 2).Any(anyValue => Text(anyValue, 1) == LoginRoute)));

    private static DecodedTracePayload DecodeTracePayload(byte[] body) =>
        new(Fields(body, 1)
            .Select(resourceSpans => new DecodedResourceSpans(
                Bytes(resourceSpans, 1),
                Fields(resourceSpans, 2)
                    .SelectMany(scopeSpans => Fields(scopeSpans, 2))
                    .Select(span => new DecodedSpan(Bytes(span, 1), Text(span, 5), Varint(span, 6)))
                    .ToList()))
            .ToList());

    private static DecodedMetricPayload DecodeMetricPayload(byte[] body) =>
        new(Fields(body, 1)
            .Select(resourceMetrics => new DecodedResourceMetrics(
                Bytes(resourceMetrics, 1),
                Fields(resourceMetrics, 2)
                    .Select(scopeMetrics => new DecodedScopeMetrics(
                        Text(Bytes(scopeMetrics, 1), 1),
                        Fields(scopeMetrics, 2)
                            .Select(metric => new DecodedMetric(Text(metric, 1), metric))
                            .ToList()))
                    .ToList()))
            .ToList());

    private static bool IsExpectedServerSpan(DecodedSpan span, string expectedTraceId) =>
        Convert.ToHexString(span.TraceId).ToLowerInvariant() == expectedTraceId
        && span.Kind == 2
        && !string.IsNullOrWhiteSpace(span.Name);

    private static bool IsCluckworkServiceResource(byte[] resource) =>
        HasStringAttribute(resource, "service.name", ServiceName);

    private static bool HasStringAttribute(byte[] attributeContainer, string key, string value) =>
        Fields(attributeContainer, 1)
            .Any(attribute => Text(attribute, 1) == key
                && Fields(attribute, 2).Any(anyValue => Text(anyValue, 1) == value));

    private static IEnumerable<byte[]> Fields(byte[] message, int fieldNumber)
    {
        var input = new CodedInputStream(message);
        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            if (WireFormat.GetTagFieldNumber(tag) == fieldNumber
                && WireFormat.GetTagWireType(tag) == WireFormat.WireType.LengthDelimited)
                yield return input.ReadBytes().ToByteArray();
            else
                input.SkipLastField();
        }
    }

    private static byte[] Bytes(byte[] message, int fieldNumber) =>
        Fields(message, fieldNumber).Single();

    private static string Text(byte[] message, int fieldNumber) =>
        Encoding.UTF8.GetString(Fields(message, fieldNumber).Single());

    private static ulong Varint(byte[] message, int fieldNumber)
    {
        var input = new CodedInputStream(message);
        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            if (WireFormat.GetTagFieldNumber(tag) == fieldNumber
                && WireFormat.GetTagWireType(tag) == WireFormat.WireType.Varint)
                return input.ReadUInt64();
            input.SkipLastField();
        }
        return 0;
    }

    private sealed record DecodedTracePayload(IReadOnlyList<DecodedResourceSpans> Resources);

    private sealed record DecodedResourceSpans(byte[] Resource, IReadOnlyList<DecodedSpan> Spans);

    private sealed record DecodedMetricPayload(IReadOnlyList<DecodedResourceMetrics> Resources);

    private sealed record DecodedResourceMetrics(byte[] Resource, IReadOnlyList<DecodedScopeMetrics> Scopes);

    private sealed record DecodedScopeMetrics(string Name, IReadOnlyList<DecodedMetric> Metrics);

    private sealed record DecodedMetric(string Name, byte[] Encoded);

    private sealed record DecodedSpan(byte[] TraceId, string Name, ulong Kind);
}
