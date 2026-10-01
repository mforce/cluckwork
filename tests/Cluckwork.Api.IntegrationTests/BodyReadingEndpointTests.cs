namespace Cluckwork.Api.IntegrationTests;

using System.IO.Pipelines;
using System.Reflection;
using Cluckwork.Api.Hosting;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

// #398 review rounds 7-8 — BindingFailureResponse.ConcernsRequestBody decides
// whether a 400 binding failure is reported under `body` or `query`, and it
// reads the ENDPOINT's contract to do it. An endpoint that declares a typed body
// parameter carries IAcceptsMetadata and is classified correctly for free. An
// endpoint that reads the raw body ITSELF declares nothing, so it must be marked
// with ReadsRequestBodyAttribute — otherwise a body failure on it is reported as
// `errors.query`, on endpoints that have no query input at all.
//
// That marker is applied by hand, and being forgotten is the DEFAULT failure:
// review caught two separate missing markers in consecutive rounds
// (PUT /accounts/logo + POST /client-errors, then POST /auth/refresh, which
// drains Request.Body to enforce its #309 cap). A third would have been found
// the same way — by a reviewer, or not at all.
//
// So the invariant is enforced from the running application's own endpoint
// table rather than trusted to a sweep: any handler that CAN reach the raw body
// must either declare a typed body, carry the marker, or be named below with a
// reason. A new endpoint taking HttpContext/HttpRequest/Stream/PipeReader fails
// this test until someone decides which it is.
[Collection(IntegrationCollection.Name)]
public sealed class BodyReadingEndpointTests(CluckworkWebApplicationFactory factory)
{
    // Parameter types through which a handler can reach the request body. A
    // handler taking none of these cannot read it, so its binding failures are
    // genuinely about the declared parameters (query, route, headers).
    private static readonly Type[] BodyCapableParameters =
        [typeof(HttpContext), typeof(HttpRequest), typeof(Stream), typeof(PipeReader)];

    // Endpoints that take one of the types above but provably do NOT read the
    // body. Each needs a reason: the point is that leaving the marker off is a
    // decision someone made, not one nobody noticed.
    private static readonly Dictionary<string, string> ReviewedAsNotReadingTheBody = new()
    {
        ["GET /api/v1/account/logo"] =
            "GetLogo takes HttpContext to set Cache-Control/ETag on the RESPONSE and to " +
            "read If-None-Match; a GET carries no body to fail on.",
        ["GET /api/v1/account/banner"] =
            "GetBanner (#179) is the same shape as GetLogo, for the same reason: HttpContext " +
            "is for the response's Cache-Control/ETag and reading If-None-Match, never the body.",
        ["POST /api/v1/auth/logout"] =
            "Logout takes HttpRequest for the refresh COOKIE and the CSRF header only — " +
            "it never touches Request.Body (unlike /auth/refresh, which drains it).",
        [" /error"] =
            "The exception-handler re-execution target. It takes HttpContext to read "
            + "IExceptionHandlerFeature and shape the response, never to read the body — and it "
            + "must stay unmarked: ConcernsRequestBody deliberately reads the feature's ORIGINAL "
            + "endpoint here, so marking `/error` would classify every failure it handles as a "
            + "body failure regardless of what was actually requested.",
    };

    private sealed record Candidate(string Key, MethodInfo Handler, EndpointMetadataCollection Metadata);

    private List<Candidate> BodyCapableEndpoints()
    {
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;
        var candidates = new List<Candidate>();

        foreach (var endpoint in endpoints.OfType<RouteEndpoint>())
        {
            // Minimal-API route handlers carry their MethodInfo in metadata.
            // Anything without one is not a handler this repo authored (health
            // checks, the SPA fallback), and has no parameters to inspect.
            var handler = endpoint.Metadata.GetMetadata<MethodInfo>();
            if (handler is null) continue;

            if (!handler.GetParameters().Any(p => BodyCapableParameters.Contains(p.ParameterType)))
                continue;

            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [];
            candidates.Add(new Candidate(
                $"{string.Join(",", methods)} {endpoint.RoutePattern.RawText}",
                handler,
                endpoint.Metadata));
        }

        return candidates;
    }

    [Fact]
    public void Every_endpoint_that_can_read_the_body_is_classified()
    {
        var candidates = BodyCapableEndpoints();

        // Guard the guard: if metadata ever stops carrying MethodInfo, or the
        // endpoint table is empty because the host changed shape, this test
        // would pass by inspecting nothing at all.
        Assert.NotEmpty(candidates);

        var unclassified = candidates
            .Where(c => c.Metadata.GetMetadata<IAcceptsMetadata>()?.RequestType is null
                && c.Metadata.GetMetadata<ReadsRequestBodyAttribute>() is null
                && !ReviewedAsNotReadingTheBody.ContainsKey(c.Key))
            .Select(c => $"{c.Key} → {c.Handler.DeclaringType?.Name}.{c.Handler.Name}")
            .ToList();

        Assert.True(unclassified.Count == 0,
            "These endpoints can reach the raw request body but say nothing about it, so a 400 "
            + "body failure on them would be reported as `errors.query`. Add "
            + "`.WithMetadata(new ReadsRequestBodyAttribute())` if the handler reads the body, or "
            + "add it to ReviewedAsNotReadingTheBody with a reason if it does not:\n  "
            + string.Join("\n  ", unclassified));
    }

    [Fact]
    public void The_reviewed_exemptions_all_still_exist()
    {
        // A renamed or deleted route would leave its exemption behind, silently
        // ready to excuse a DIFFERENT endpoint that later takes the same key.
        var keys = BodyCapableEndpoints().Select(c => c.Key).ToHashSet();
        var stale = ReviewedAsNotReadingTheBody.Keys.Where(k => !keys.Contains(k)).ToList();

        Assert.True(stale.Count == 0,
            "These exemptions no longer match a body-capable endpoint — the route was renamed or "
            + "removed. Delete them:\n  " + string.Join("\n  ", stale));
    }
}
