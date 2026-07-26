namespace Cluckwork.Api.Endpoints.ClientErrors;

using System.Text.Json;
using Cluckwork.Api.RateLimiting;
using Microsoft.AspNetCore.Http.Features;

// #217 — the SPA's ErrorBoundary reports render crashes here so the operator
// learns a screen is crashing without a support screenshot. The report is
// WRITTEN TO THE LOG and stored nowhere else: no table, no retention question,
// and the existing log pipeline (console, OTLP when enabled) carries it.
//
// Everything about the endpoint assumes a hostile caller, because it is
// anonymous by design — the login screen can crash too, and a crashing
// authenticated app may no longer hold a usable token. Per-IP rate limit
// (#143 infrastructure), a total byte cap read under our control, and
// per-field truncation bound what one address can push into the log.
public static class ClientErrorEndpoints
{
    // Total request-body cap. A real report is a few KB of stack; 16 KB gives
    // slack for deep component trees without letting one POST carry a novel.
    public const int MaxReportBytes = 16 * 1024;

    // Per-field bounds applied AFTER the byte cap: the cap limits the request,
    // these keep any single log line readable and bounded even at the cap.
    private const int MaxMessageChars = 2000;
    private const int MaxStackChars = 8000;
    private const int MaxRouteChars = 500;
    private const int MaxShortChars = 100;

    public static RouteGroupBuilder MapClientErrorEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/", Report)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitingOptions.ClientErrorsPolicyName)
            .WithName("ReportClientError")
            .WithSummary("Accept a browser error report and write it to the server log.");
        return group;
    }

    // Bound manually (HttpRequest, not a [FromBody] DTO): model binding would
    // buffer an arbitrarily large body before our code ran; here the read loop
    // below is the size guarantee, same pattern as the logo upload (#123).
    private static async Task<IResult> Report(
        HttpRequest request, ILogger<ClientErrorReport> logger, CancellationToken ct)
    {
        // A declared oversize is refused without reading a byte. Content-Length
        // is only a claim, which is why the read below is capped as well.
        if (request.ContentLength > MaxReportBytes)
            return TooLarge();

        // Best-effort transport cutoff (absent under TestServer, read-only once
        // the body is touched); the loop bound is the guarantee, not this.
        var sizeLimit = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeLimit is { IsReadOnly: false })
            sizeLimit.MaxRequestBodySize = MaxReportBytes;

        // One byte of headroom: filling past MaxReportBytes proves the body is
        // over the cap without reading whatever else the client meant to send.
        var buffer = new byte[MaxReportBytes + 1];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await request.Body.ReadAsync(buffer.AsMemory(read, buffer.Length - read), ct);
            if (n == 0) break;
            read += n;
        }
        if (read > MaxReportBytes)
            return TooLarge();

        ClientErrorReport? report;
        try
        {
            report = JsonSerializer.Deserialize<ClientErrorReport>(
                buffer.AsSpan(0, read), JsonOptions);
        }
        catch (JsonException)
        {
            return Results.Problem(
                title: "Malformed report", statusCode: StatusCodes.Status400BadRequest,
                detail: "The error report is not valid JSON.");
        }

        if (report is null || string.IsNullOrWhiteSpace(report.Message))
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["message"] = ["A non-empty message is required."]
            });

        var scope = report.Scope?.ToLowerInvariant();
        if (scope is not ("app" or "screen"))
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["scope"] = ["Scope must be \"app\" or \"screen\"."]
            });

        // Template carries the operator-facing line; the bulky fields ride as
        // structured properties via the provider's scope handling (#216), so
        // one query key (Scope/Route/ClientTraceId) finds them without the
        // stacks shouting in the console template.
        using (logger.BeginScope(new Dictionary<string, object?>
        {
            ["Stack"] = Truncate(report.Stack, MaxStackChars),
            ["ComponentStack"] = Truncate(report.ComponentStack, MaxStackChars),
            ["AppVersion"] = Truncate(report.AppVersion, MaxShortChars),
            ["ClientTraceId"] = Truncate(report.TraceId, MaxShortChars)
        }))
        {
            logger.LogError("Client error ({Scope}) at {Route}: {Message}",
                scope,
                Truncate(report.Route, MaxRouteChars) ?? "(unknown)",
                Truncate(report.Message, MaxMessageChars));
        }

        return Results.Accepted();
    }

    private static IResult TooLarge() => Results.Problem(
        title: "Report too large", statusCode: StatusCodes.Status413PayloadTooLarge,
        detail: $"Error reports are capped at {MaxReportBytes} bytes.");

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
}

// The report shape the SPA sends. Everything but the message and scope is
// optional: a crash report with holes still beats silence.
public sealed record ClientErrorReport(
    string? Message,
    string? Stack,
    string? ComponentStack,
    string? Scope,
    string? Route,
    string? AppVersion,
    string? TraceId);
