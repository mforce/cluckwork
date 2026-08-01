using Cluckwork.Api;
using Cluckwork.Api.Cli;
using Cluckwork.Api.Endpoints.Accounts;
using Cluckwork.Api.Endpoints.Audit;
using Cluckwork.Api.Endpoints.Auth;
using Cluckwork.Api.Endpoints.Catalog;
using Cluckwork.Api.Endpoints.ClientErrors;
using Cluckwork.Api.Endpoints.Customers;
using Cluckwork.Api.Endpoints.DailyEntries;
using Cluckwork.Api.Endpoints.EggGrades;
using Cluckwork.Api.Endpoints.Expenses;
using Cluckwork.Api.Endpoints.Export;
using Cluckwork.Api.Endpoints.Flocks;
using Cluckwork.Api.Endpoints.Inventory;
using Cluckwork.Api.Endpoints.Me;
using Cluckwork.Api.Endpoints.Reports;
using Cluckwork.Api.Endpoints.Sales;
using Cluckwork.Api.Endpoints.Stock;
using Cluckwork.Api.Endpoints.Users;
using Cluckwork.Api.Endpoints.Water;
using Cluckwork.Api.Hosting;
using Cluckwork.Api.Middleware;
using Cluckwork.Api.Security;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Serilog;

// #266 — the `healthcheck` verb is the container HEALTHCHECK probe. It only GETs
// the already-running serving process's /health/ready over loopback, so — unlike
// the migrate/seed/recover-admin verbs, which operate ON the built host via
// CliDispatcher after Build() — it needs no host, DI, DB or config. Dispatch it
// HERE, before anything is built, so a 30s HEALTHCHECK never pays a full app
// startup, re-validates the connection string, or re-logs boot warnings on every
// tick. (The other verbs can't move up: they require the built host's services.)
if (args is [HealthCheckCliCommand.Verb, ..])
    return await HealthCheckCliCommand.RunAsync(args);

var builder = WebApplication.CreateBuilder(args);

var telemetry = builder.Services.AddCluckworkTelemetry(
    builder.Configuration, builder.Environment, !CliDispatcher.IsCliInvocation(args));

var persistence = builder.Services.AddCluckworkPersistence(
    builder.Configuration,
    builder.Environment);

builder.Services.AddCluckworkIdentity(builder.Configuration);

var rateLimiting = builder.Services.AddCluckworkRateLimiting(
    builder.Configuration);
builder.Services.AddCluckworkEdgeSecurity(rateLimiting.TrustedProxies);

builder.Services.AddCluckworkFeatures(builder.Configuration);

// #307 — lease duration / max-wait bounds for the idempotency claim protocol.
builder.Services.Configure<IdempotencyOptions>(
    builder.Configuration.GetSection(IdempotencyOptions.SectionName));

// --- OpenAPI ---
builder.Services.AddOpenApi();

builder.Services.AddCluckworkHealthChecks();
builder.Services.AddCluckworkJobs();

// ----------------------------------------------------------------
var app = builder.Build();

// #262 — replay the connection-string TLS warnings once, now that a logger exists (a
// floor violation already failed the boot above during configuration). Logged before the
// CLI dispatch so the migrate/seed one-shot verbs surface it too.
foreach (var connectionStringWarning in persistence.ConnectionStringWarnings)
    app.Logger.LogWarning("{ConnectionStringWarning}", connectionStringWarning);

// One-off operator commands (seed / migrate / recover-admin) run then EXIT
// before the web host starts — Kestrel and the hosted services never run for
// these. Each lives in Cluckwork.Api.Cli; the dispatcher returns the exit
// code, or null when no CLI verb matched (a normal serving start). Extracted
// from the ~180 inline lines that used to sit here (#288).
if (await CliDispatcher.TryRunAsync(app, args) is int cliExitCode)
    return cliExitCode;

// #260 — proxy-trust boot guard, SERVING process only. The forwarded-headers
// middleware above honours X-Forwarded-Proto/-For solely from the trustedProxies
// networks; with that list empty in Production two controls silently go inert:
// HSTS (#144) never sees the real HTTPS scheme and stops emitting, and the per-IP
// login rate limiter (#143) collapses to one global bucket (every request looks
// like it came from the proxy hop). Fail the boot loudly rather than run degraded.
// Placed AFTER the CLI dispatcher's return so the one-off migrate/seed/recover-admin
// verbs — which never serve traffic — are unaffected. Opt out only for a rare
// direct-TLS-exposure deploy via RateLimiting:AllowNoTrustedProxies.
// Gated on IsProduction() (not !IsDevelopment()) deliberately: the integration
// Testing env is also empty-proxied and must still boot; a real Staging serving
// env, if ever introduced, would be added to this gate.
if (app.Environment.IsProduction()
    && rateLimiting.TrustedProxies.Length == 0
    && !rateLimiting.Options.AllowNoTrustedProxies)
{
    throw new InvalidOperationException(
        "RateLimiting:TrustedProxies is empty in Production, so the app trusts no "
        + "proxy's X-Forwarded-* headers. Two security controls then silently go "
        + "inert: HSTS (#144) never sees the real HTTPS scheme and stops emitting, "
        + "and the per-IP login rate limiter (#143) collapses to a single global "
        + "bucket. Fix ONE of: (1) set RateLimiting:TrustedProxies to the edge "
        + "proxy/load-balancer network CIDR (the hop that terminates TLS and adds "
        + "X-Forwarded-*); or (2) for a rare deploy that terminates TLS itself with "
        + "no fronting proxy, set RateLimiting:AllowNoTrustedProxies=true to "
        + "acknowledge the direct-exposure trade-off and boot anyway.");
}

// #319 — AllowedHosts boot guard, SERVING process only. appsettings.json defaults
// AllowedHosts to "*"; a deploy that omits or misnames the host variable (a blank
// ${CLUCKWORK_HOST} substitution was observed) then silently disables Host-header
// filtering (#144) and a forged Host header is accepted. Fail the boot loudly
// unless a concrete public host is pinned. Loopback is force-added for health
// probes later (AddCluckworkEdgeSecurity), so it need not appear in config here.
// Placed AFTER the CLI dispatcher's return (like #260) so the one-off migrate/
// seed/recover-admin verbs are unaffected; healthcheck already early-dispatches.
if (app.Environment.IsProduction())
{
    var configuredHosts = (builder.Configuration["AllowedHosts"] ?? string.Empty)
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var hasConcretePublicHost = configuredHosts.Length > 0 && configuredHosts.All(h => h != "*");
    if (!hasConcretePublicHost)
        throw new InvalidOperationException(
            "AllowedHosts is missing, blank, or wildcard ('*') in Production, so Host-header "
            + "filtering (#144) is off and a forged Host header is accepted. Set AllowedHosts to "
            + "the concrete public hostname the app serves (the deployment supplies it as "
            + "CLUCKWORK_HOST). Loopback (localhost/127.0.0.1/[::1]) is always allowed for "
            + "container health probes, so it need not be listed.");
}

// One boot line makes export misconfiguration observable — a typo'd env var
// name otherwise silently disables the whole pipeline (#226 review).
if (telemetry.TraceEndpoint is not null)
    app.Logger.LogInformation(
        "OTLP export enabled: traces -> {OtlpTraceEndpoint}, metrics -> {OtlpMetricsEndpoint} ({OtlpProtocol})",
        telemetry.TraceEndpoint,
        telemetry.MetricsEndpoint,
        telemetry.Protocol);
else
    app.Logger.LogInformation("OTLP export disabled (Otlp:Endpoint not set)");
// ----------------------------------------------------------------

// --- Startup: apply migrations (idempotent) ---
// Database:MigrateOnStartup (default true, but Production sets it false —
// #263) gates schema DDL. #283 — there is no runtime seeder to run after it:
// the base reference data (roles, default egg grades, the default account)
// ships AS PART OF the migrations themselves via raw migrationBuilder.Sql with
// WHERE NOT EXISTS guards (NOT EF's InsertData/HasData), so a freshly migrated
// database is already usable with no further boot-time step and no Seed:*
// config. The first admin is provisioned separately, out of band, by the
// one-shot `bootstrap-admin` command (never a serving-boot side effect — see
// Cli/BootstrapAdminCliCommand.cs).
{
    using var startupScope = app.Services.CreateScope();
    var sp = startupScope.ServiceProvider;
    if (builder.Configuration.GetValue("Database:MigrateOnStartup", true))
        await sp.GetRequiredService<AppDbContext>().Database.MigrateAsync();
}

// Resolve the real client IP and scheme from a trusted proxy's forwarded
// headers before anything reads RemoteIpAddress or Request.Scheme (the rate
// limiter, HTTPS redirection and HSTS all depend on it) — #143/#144.
app.UseForwardedHeaders();

// Security response headers on every response (#144). Outermost after the
// forwarded headers so it also covers static files and error responses.
app.UseSecurityHeaders();

// #312 — default private/no-store Cache-Control on every response (API reads,
// writes, auth, validation, errors, exports). Same outermost placement as
// UseSecurityHeaders and for the same reason: it must also cover the
// exception-handler re-execution and any response produced below, while still
// letting a downstream stage's own deliberate Cache-Control (static assets,
// the SPA fallback, the farm logo's revalidate policy) win via TryAdd.
app.UseDefaultResponseCaching();

// HSTS outside Development — only meaningful now the forwarded proto is trusted
// so Request.IsHttps reflects the real client scheme.
if (!app.Environment.IsDevelopment())
    app.UseHsts();

app.UseExceptionHandler(new ExceptionHandlerOptions
{
    AllowStatusCode404Response = true,
    ExceptionHandlingPath = "/error"
});

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseHttpsRedirection();

// Serve the built SPA (copied to wwwroot in the Docker image). Static assets are
// public — mounted before auth. API routes and the SPA fallback are wired below.
// #141 — hashed /assets/* are immutable-forever, index.html revalidates, so a
// fronting CDN (and browsers) can cache aggressively without serving a stale app.
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = StaticAssetCaching.ApplyCacheHeaders
});

// One structured completion line per request (#214): method, path, status,
// elapsed — the request's TraceId rides on every event via Serilog. Mounted
// after static files so hashed-asset hits don't flood the log; health probes
// are demoted below Information for the same reason.
app.UseSerilogRequestLogging(options =>
{
    // Pin the middleware to THIS host's logger. Its default is the process-wide
    // static Log.Logger, which any co-hosted Serilog app (the integration-test
    // suite runs many) reassigns and disposes — completions would silently go
    // to another host's pipeline.
    options.Logger = app.Services.GetRequiredService<Serilog.ILogger>();
    options.GetLevel = (httpContext, _, exception) =>
        // Health first — a FAILING probe (503 during a DB outage) must not
        // escalate to Error while orchestrators poll every few seconds. The
        // exception-handler re-execution at /error is demoted too: the
        // original request already logged its Error completion, a second
        // line would double-count every failed request.
        httpContext.Request.Path.StartsWithSegments("/health")
        || httpContext.Features.Get<IExceptionHandlerFeature>() is not null
            ? Serilog.Events.LogEventLevel.Verbose
            : exception is not null || httpContext.Response.StatusCode >= 500
                ? Serilog.Events.LogEventLevel.Error
                : Serilog.Events.LogEventLevel.Information;
});

// Endpoint rate-limit policies (#143) — no global limiter, only routes that
// opt in via RequireRateLimiting are affected.
app.UseRateLimiter();

// #309 — enforce the per-endpoint request-body byte caps (the auth/credential
// endpoints opt in via WithMaxRequestBodyBytes). Placed AFTER Serilog request
// logging and the rate limiter — NOT right after UseExceptionHandler — so a
// declared-oversize body (the cheapest attack: no need to stream anything) is
// still logged (#214's one-line-per-request contract) and still consumes a
// login rate-limit permit (#143) before this middleware returns early; an
// earlier placement let an attacker flood oversized bodies at unlimited rate
// while every legitimate-sized attempt was throttled. Still ahead of
// auth/tenant/idempotency/binding/the PBKDF2 hasher — routing has already run
// (this is well after UseExceptionHandler), so the matched endpoint's metadata
// is available, and an over-limit body is refused before any of that work.
app.UseCluckworkRequestBodyLimit();

app.UseAuthentication();
app.UseMiddleware<TenantResolutionMiddleware>();
// #283 — the first-run "you must set a new password" gate. BEFORE
// UseAuthorization (deliberately) so it applies uniformly regardless of which
// AuthPolicies tier an endpoint carries, and before idempotency so a blocked
// write never consumes a key.
app.UseMiddleware<MustChangePasswordMiddleware>();
// Authorization must run BEFORE idempotency: the replay path returns cached
// responses without invoking the endpoint, so a role-denied caller replaying
// an admin's key must hit the 403 first (codex review of PR #78).
app.UseAuthorization();
app.UseMiddleware<IdempotencyMiddleware>();

// --- Endpoint groups (URL versioned: /api/v1/...) ---
app.MapGroup("/api/v1/auth")
    .WithTags("Auth")
    // Rate-limit policies are applied per endpoint inside MapAuthEndpoints:
    // login and refresh differ, and authenticated /logout is not limited (#143).
    .MapAuthEndpoints();

app.MapGroup("/api/v1/flocks")
    .WithTags("Flocks")
    .RequireAuthorization()
    .MapFlockEndpoints();

app.MapGroup("/api/v1/egg-grades")
    .WithTags("EggGrades")
    .RequireAuthorization()
    .MapEggGradeEndpoints();

// Product catalog + packed-unit conversions (#97): writes admin-gated inside.
app.MapGroup("/api/v1/products")
    .WithTags("Catalog")
    .RequireAuthorization()
    .MapProductEndpoints();

app.MapGroup("/api/v1/egg-unit-conversions")
    .WithTags("Catalog")
    .RequireAuthorization()
    .MapEggUnitConversionEndpoints();

// Money data — admin end to end (#87), reads included.
app.MapGroup("/api/v1/expense-categories")
    .WithTags("Expenses")
    .RequireAuthorization(AuthPolicies.AdminOnly)
    .MapExpenseCategoryEndpoints();

app.MapGroup("/api/v1/expenses")
    .WithTags("Expenses")
    .RequireAuthorization(AuthPolicies.AdminOnly)
    .MapExpenseEndpoints();

app.MapGroup("/api/v1/account")
    .WithTags("Account")
    .RequireAuthorization()
    .MapAccountEndpoints()
    .MapFarmLogoEndpoints();

// #45 — user-scoped sibling of /account: identity comes from the JWT, not the
// farm. DEFAULT policy (not a named one) so every role, ReadOnly included, can
// read their own identity and language preference.
app.MapGroup("/api/v1/me")
    .WithTags("Me")
    .RequireAuthorization()
    .MapMeEndpoints();

app.MapGroup("/api/v1/inventory")
    .WithTags("Inventory")
    .RequireAuthorization()
    .MapInventoryEndpoints();

app.MapGroup("/api/v1/water-usage")
    .WithTags("WaterUsage")
    .RequireAuthorization()
    .MapWaterUsageEndpoints();

app.MapGroup("/api/v1/daily-entries")
    .WithTags("DailyEntries")
    .RequireAuthorization()
    .MapDailyEntryEndpoints();

app.MapGroup("/api/v1/stock")
    .WithTags("Stock")
    .RequireAuthorization()
    .MapStockEndpoints();

app.MapGroup("/api/v1/customers")
    .WithTags("Customers")
    .RequireAuthorization()
    .MapCustomerEndpoints()
    .MapCustomerBalanceEndpoints();

app.MapGroup("/api/v1/sales")
    .WithTags("Sales")
    .RequireAuthorization()
    .MapSaleEndpoints()
    .MapOrderPaymentEndpoints();

// Payments are the Sales role's job (spec §5.1, #103): Owner/Manager/Sales.
// The rest of the money tier (expenses, money reports, audit, export) stays
// Owner/Manager.
app.MapGroup("/api/v1/payments")
    .WithTags("Payments")
    .RequireAuthorization(AuthPolicies.SalesAccess)
    .MapPaymentEndpoints();

// Reports (#91): production is open; the money routes carry their own
// AdminOnly inside the group.
app.MapGroup("/api/v1/reports")
    .WithTags("Reports")
    .RequireAuthorization()
    .MapReportEndpoints();

// Audit trail (#93): read-only, admin-only.
app.MapGroup("/api/v1/audit")
    .WithTags("Audit")
    .RequireAuthorization(AuthPolicies.AdminOnly)
    .MapAuditEndpoints();

// User management is the OWNER's alone (#103) — Managers run the farm, the
// Owner decides who works on it.
app.MapGroup("/api/v1/users")
    .WithTags("Users")
    .RequireAuthorization(AuthPolicies.OwnerOnly)
    .MapUserEndpoints();

// Manual backup (#95): CSV export, read-only, admin-only.
app.MapGroup("/api/v1/export")
    .WithTags("Export")
    .RequireAuthorization(AuthPolicies.AdminOnly)
    .MapExportEndpoints();

// #217 — browser error reports. Anonymous (the login screen can crash too);
// the endpoint carries its own per-IP rate limit and size cap inside.
app.MapGroup("/api/v1/client-errors")
    .WithTags("ClientErrors")
    .MapClientErrorEndpoints();

// Health: live = the process runs (no checks); ready = dependencies too.
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false
});
app.MapHealthChecks("/health/ready");

app.Map("/error", (HttpContext context) =>
{
    var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    return exception switch
    {
        // Minimal-API body binding failures (malformed JSON, unparseable
        // dates/guids) throw this with a 400 — without the mapping the
        // exception handler swallowed it into a 500.
        BadHttpRequestException bad => Results.Problem(
            detail: bad.Message,
            statusCode: bad.StatusCode,
            title: "Invalid request body"),
        DbUpdateConcurrencyException => Results.Conflict(new ProblemDetails
        {
            Title = "Concurrency conflict",
            Detail = "The resource was modified by another request. Reload the current state and retry.",
            Status = StatusCodes.Status409Conflict
        }),
        // Unique-constraint and FK violations (e.g. concurrent insert of the same natural key).
        DbUpdateException => Results.Problem(
            detail: "The request conflicts with existing data.",
            statusCode: StatusCodes.Status409Conflict,
            title: "Data conflict"),
        // Locking paths (confirm/void) share one canonical lock order so this
        // shouldn't fire; if it ever does, it's a retryable conflict, not a 500.
        // 40001 is a serialization failure — reachable from the snapshot-
        // isolated reads (the export's RepeatableRead transaction). Postgres
        // could not order this transaction against a concurrent one, which is
        // a retryable conflict, not a fault.
        Npgsql.PostgresException { SqlState: "40P01" or "40001" } => Results.Problem(
            detail: "The request conflicted with a concurrent operation. Retry.",
            statusCode: StatusCodes.Status409Conflict,
            title: "Concurrency conflict"),
        _ => Results.Problem()
    };
});

// An unknown /api/* path must 404 as an API error — NOT fall through to the SPA
// fallback below (which would return index.html, i.e. a 200 text/html page, and
// #141 would then stamp a Cache-Control header on an /api response). Real
// endpoints have literal segments and outrank this catch-all; the SPA fallback
// is a bare non-file catch-all, so this /api-prefixed one wins for /api paths.
app.Map("/api/{**rest}", () => Results.Problem(
    statusCode: StatusCodes.Status404NotFound, title: "Not found"))
    .ExcludeFromDescription();

// #266 — same guard for /health/*: an unknown health path must 404, NOT fall
// through to the SPA fallback (which returns index.html as a 200 text/html page).
// Otherwise a removed/renamed /health/ready would be silently shadowed by a
// 200 — and the container HEALTHCHECK probe (which accepts any 2xx) would report
// a dead app HEALTHY. The literal /health/live + /health/ready above outrank this
// catch-all; only unmatched /health/* paths hit it.
app.Map("/health/{**rest}", () => Results.Problem(
    statusCode: StatusCodes.Status404NotFound, title: "Not found"))
    .ExcludeFromDescription();

// SPA client-side routing: any non-API, non-file request falls back to
// index.html. Lowest route priority, so the /api/v1 endpoints and /health above
// always match first. No-op in dev (no wwwroot) — dev uses the Vite server.
// #141 — the fallback ALWAYS serves index.html, so it unconditionally emits
// no-cache (AlwaysRevalidateHeader): a new deploy propagates immediately even
// through a fronting CDN, and a missing /assets/x can never be pinned immutable.
app.MapFallbackToFile("index.html", new StaticFileOptions
{
    OnPrepareResponse = StaticAssetCaching.AlwaysRevalidateHeader
});

app.Run();
return 0;

// Exposes Program for WebApplicationFactory in integration tests
public partial class Program { }
