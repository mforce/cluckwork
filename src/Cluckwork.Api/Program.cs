using System.Security.Cryptography;
using Cluckwork.Api.Endpoints.Auth;
using Cluckwork.Api.Endpoints.DailyEntries;
using Cluckwork.Api.Endpoints.Sales;
using Cluckwork.Api.Middleware;
using Cluckwork.Application.Common;
using Cluckwork.Application.Features.DailyEntries;
using Cluckwork.Application.Features.DailyEntries.RecordDailyEntry;
using Cluckwork.Application.Features.EggLots;
using Cluckwork.Application.Features.Sales;
using Cluckwork.Application.Features.Sales.ConfirmSale;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Jobs;
using Cluckwork.Infrastructure.Persistence;
using Cluckwork.Infrastructure.Persistence.Interceptors;
using Cluckwork.Infrastructure.Providers;
using Cluckwork.Infrastructure.Providers.Postgres;
using Cluckwork.Infrastructure.Repositories;
using Cluckwork.Infrastructure.Time;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// --- Logging ---
builder.Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration));

// --- OpenTelemetry ---
builder.Services.AddOpenTelemetry()
    .WithTracing(trace => trace
        .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("Cluckwork.Api"))
        .AddAspNetCoreInstrumentation()
        .AddEntityFrameworkCoreInstrumentation());

// --- Multi-tenancy (scoped per request) ---
builder.Services.AddScoped<TenantContext>();

// --- EF Core ---
var dbProvider = builder.Configuration["Database:Provider"] ?? "Postgres";
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Connection string 'Default' is not configured.");

builder.Services.AddScoped<TenantStampInterceptor>();
builder.Services.AddDbContext<AppDbContext>((sp, options) =>
{
    options.AddInterceptors(sp.GetRequiredService<TenantStampInterceptor>());
    IDbProviderConfigurator configurator = dbProvider switch
    {
        "Postgres" => new PostgresDbContextConfigurator(),
        _ => throw new NotSupportedException($"Unsupported database provider: {dbProvider}")
    };
    configurator.Configure(options, connectionString);
});

// --- ASP.NET Core Identity ---
builder.Services
    .AddIdentityCore<ApplicationUser>(opts =>
    {
        opts.Password.RequiredLength = 12;
        opts.Lockout.MaxFailedAccessAttempts = 5;
    })
    .AddRoles<ApplicationRole>()
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders();

// --- JWT Bearer (asymmetric signing; tech spec §7.4) ---
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
var jwtPublicKeyPem = PemKey.Normalize(builder.Configuration["Jwt:PublicKeyPem"]
    ?? throw new InvalidOperationException("Jwt:PublicKeyPem is not configured."));

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opts =>
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(jwtPublicKeyPem);

        opts.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidateAudience = true,
            ValidAudience = builder.Configuration["Jwt:Audience"],
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new RsaSecurityKey(rsa),
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });

builder.Services.AddAuthorization();

// --- Application ports ---
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddScoped<IClock, SystemClock>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IIdentityProvider, IdentityProvider>();
builder.Services.AddScoped<IJwtTokenService, JwtTokenService>();
builder.Services.AddScoped<IDailyEntryRepository, DailyEntryRepository>();
builder.Services.AddScoped<IEggLotRepository, EggLotRepository>();
builder.Services.AddScoped<ISalesOrderRepository, SalesOrderRepository>();

// --- Validators ---
builder.Services.AddScoped<IValidator<RecordDailyEntryCommand>, RecordDailyEntryValidator>();

// --- Handlers (direct — no mediator, tech spec §2.1) ---
builder.Services.AddScoped<RecordDailyEntryHandler>();
builder.Services.AddScoped<ConfirmSaleHandler>();

// --- OpenAPI ---
builder.Services.AddOpenApi();

// --- Health checks ---
builder.Services.AddHealthChecks();

// --- Durable job scaffold (tech spec §9) ---
builder.Services.AddHostedService<DurableJobWorker>();

// ----------------------------------------------------------------
var app = builder.Build();
// ----------------------------------------------------------------

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
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseMiddleware<IdempotencyMiddleware>();
app.UseAuthorization();

// --- Endpoint groups (URL versioned: /api/v1/...) ---
app.MapGroup("/api/v1/auth")
    .WithTags("Auth")
    .MapAuthEndpoints();

app.MapGroup("/api/v1/daily-entries")
    .WithTags("DailyEntries")
    .RequireAuthorization()
    .MapDailyEntryEndpoints();

app.MapGroup("/api/v1/sales")
    .WithTags("Sales")
    .RequireAuthorization()
    .MapSaleEndpoints();

// Health
app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");

app.Map("/error", (HttpContext context) =>
{
    var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    return exception switch
    {
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
        _ => Results.Problem()
    };
});

// SPA client-side routing: any non-API, non-file request falls back to
// index.html. Lowest route priority, so the /api/v1 endpoints and /health above
// always match first. No-op in dev (no wwwroot) — dev uses the Vite server.
app.MapFallbackToFile("index.html");

app.Run();

// Exposes Program for WebApplicationFactory in integration tests
public partial class Program { }
