namespace Cluckwork.Api.Hosting;

using Cluckwork.Api.Validation;
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
    public static bool ConcernsRequestBody(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<IAcceptsMetadata>()?.RequestType is not null
        || context.Request.ContentLength > 0
        || context.Request.Headers.TransferEncoding.Count > 0;
}
