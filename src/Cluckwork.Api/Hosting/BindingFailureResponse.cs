namespace Cluckwork.Api.Hosting;

using Cluckwork.Api.Validation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Metadata;

// #398 review (Codex) — RouteHandlerOptions.ThrowOnBadRequest is forced true
// in every environment (see Program.cs), so a JSON-binding failure (a
// fractional quantity into an int, an unparseable date/guid, malformed JSON
// syntax, …) throws a BadHttpRequestException deep inside minimal-API's
// generated body-reader instead of the framework silently setting a 400 and
// returning. Before this middleware existed, that exception propagated all
// the way up through UseSerilogRequestLogging (registered ABOVE this
// middleware in Program.cs's pipeline) before UseExceptionHandler (registered
// above THAT) finally caught it and mapped it to the correct 400
// ValidationProblem at /error. Serilog.AspNetCore's RequestLoggingMiddleware
// catches ANY exception that passes through it and logs that request's
// completion with a HARDCODED StatusCode 500 at Error — so an ordinary
// malformed body (a fractional quantity, a bad date, scanner traffic) was
// logged as a 500 server fault while the client correctly received a 400.
// That inflated 5xx/error telemetry and would trigger false alerts;
// RequestLoggingTests pins the fix.
//
// Registered in Program.cs IMMEDIATELY AFTER UseSerilogRequestLogging — i.e.
// INSIDE it in the pipeline — so THIS middleware is the first thing to see
// the exception. It catches it, writes the real 400 body itself, and
// (critically) does NOT rethrow: control returns to Serilog with a normal
// 400 response and no exception in flight, so GetLevel's ordinary
// "exception is not null || StatusCode >= 500" check sees neither and logs
// Information — exactly like any other well-formed 400. No re-execution at
// /error happens either, since UseExceptionHandler (which sits ABOVE Serilog)
// never sees anything propagate that far.

// #398 review round 7 (Codex) — marks an endpoint that reads the request body
// MANUALLY (raw stream, or a bound `HttpRequest`) instead of declaring a typed
// body parameter. Those carry no IAcceptsMetadata, so without this marker
// ConcernsRequestBody would classify a body failure on them as a query one —
// and they have no query input at all.
//
// Deliberately a private marker rather than `.Accepts<T>(contentType)`:
// Accepts also imposes a CONTENT-TYPE CONSTRAINT ON THE ROUTE, so adding it
// would change which requests match and could start 404/415-ing callers that
// work today. This affects only how a failure is described.
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class ReadsRequestBodyAttribute : Attribute;

public static class BindingFailureResponse
{
    public static IApplicationBuilder UseCluckworkBindingFailureResponse(this IApplicationBuilder app) =>
        app.Use(static async (context, next) =>
        {
            try
            {
                await next();
            }
            catch (BadHttpRequestException ex) when (ex.StatusCode < StatusCodes.Status500InternalServerError)
            {
                // A NON-400 BadHttpRequestException — 413 from the #309
                // request-body cap's rare non-JSON-bound-endpoint escape, 415
                // from an unrecognised Content-Type, … — is NOT this
                // middleware's concern. Rethrow untouched so it keeps
                // flowing to the existing /error mapping exactly as before
                // this change: RequestBodyLimit.cs's WriteBodyTooLargeAsync
                // depends on THAT handler staying byte-identical to its own
                // 413 shape.
                //
                // Same for a response that has already started: writing here
                // would throw a second, more confusing exception on top of
                // the first (Response.HasStarted), so rethrow and let it
                // propagate as an ordinary unhandled exception instead.
                if (ex.StatusCode != StatusCodes.Status400BadRequest || context.Response.HasStarted)
                    throw;

                // #398 review round 4 (Codex) — a failed TYPED QUERY PARAMETER
                // throws this same 400, so blaming the body unconditionally lies
                // to the caller of a bodyless GET.
                //
                // Round 5 (Codex) corrected HOW to tell them apart. The first
                // attempt asked whether the REQUEST carried payload bytes, which
                // inverts for the case that matters most: a caller who omits a
                // REQUIRED body sends no bytes, yet that is a body failure, and
                // it was being reported as a query one.
                //
                // So ask the ENDPOINT what it accepts, not the request what it
                // sent. IAcceptsMetadata is present with a RequestType exactly
                // when a route declares a request body — for those, a binding
                // failure is a body failure whether the body was malformed or
                // absent. The byte check stays only as a fallback for the
                // (unexpected) case of no endpoint metadata.
                //
                // Still deliberately NOT ex.Message: the binding messages are
                // framework internals that differ per binding source and can
                // change between versions.
                await ValidationResponse
                    .BindingFailureProblem(ConcernsRequestBody(context))
                    .ExecuteAsync(context);
            }
        });

    // Shared by the middleware above and Program.cs's `/error` backstop, so the
    // two can never disagree about which key a binding failure is reported
    // under — the same reason the response shape itself is a single factory.
    //
    // #398 review round 6 (Codex) — the byte checks are a fallback for "no
    // endpoint was matched", and must therefore be BRANCHED, not OR-ed. Or-ing
    // them let incidental payload bytes on a query-only route report `body`:
    // a GET to /api/v1/reports/production?from=not-a-date carrying a non-empty
    // body has ContentLength > 0, so the expression returned true even though
    // that endpoint accepts no body at all. The comment claimed "fallback"
    // while the code applied the checks unconditionally — the code is now what
    // the comment says.
    //
    // When an endpoint IS matched, its own contract is authoritative and
    // nothing about the request can override it.
    public static bool ConcernsRequestBody(HttpContext context)
    {
        // #398 review round 8 (found by BodyReadingEndpointTests, not by report)
        // — the `/error` backstop calls this from INSIDE exception-handler
        // re-execution, and that re-execution clears the endpoint and routes
        // again, so context.GetEndpoint() there is the `/error` endpoint itself.
        // `/error` declares no body and carries no marker, so once round 6 made a
        // matched endpoint authoritative, the backstop answered `query` for
        // EVERY failure it handled, including a malformed JSON body.
        //
        // IExceptionHandlerFeature.Endpoint is the framework's own record of
        // what was matched before the throw — the original contract, which is
        // the one this question is about. Outside re-execution the feature is
        // absent and the current endpoint is already the right one.
        var endpoint = context.Features.Get<IExceptionHandlerFeature>()?.Endpoint
            ?? context.GetEndpoint();
        if (endpoint is not null)
            // IAcceptsMetadata covers endpoints with a declared typed body.
            // ReadsRequestBodyAttribute covers the ones that read it manually
            // and therefore declare nothing — see the attribute's own comment.
            return endpoint.Metadata.GetMetadata<IAcceptsMetadata>()?.RequestType is not null
                || endpoint.Metadata.GetMetadata<ReadsRequestBodyAttribute>() is not null;

        // No endpoint (404, or a failure before routing resolved one): fall
        // back to whether the request carried a payload at all.
        return context.Request.ContentLength > 0
            || context.Request.Headers.TransferEncoding.Count > 0;
    }
}
