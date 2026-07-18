using System.Security.Cryptography;
using Cluckwork.Api.Endpoints.Auth;
using Cluckwork.Api.Endpoints.Customers;
using Cluckwork.Api.Endpoints.DailyEntries;
using Cluckwork.Api.Endpoints.EggGrades;
using Cluckwork.Api.Endpoints.Flocks;
using Cluckwork.Api.Endpoints.Sales;
using Cluckwork.Api.Endpoints.Stock;
using Cluckwork.Api.Middleware;
using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Accounts;
using Cluckwork.Application.Features.Customers;
using Cluckwork.Application.Features.Customers.CreateCustomer;
using Cluckwork.Application.Features.DailyEntries;
using Cluckwork.Application.Features.DailyEntries.RecordDailyEntry;
using Cluckwork.Application.Features.DailyEntries.SubmitDailyEntry;
using Cluckwork.Application.Features.EggGrades;
using Cluckwork.Application.Features.EggGrades.CreateEggGrade;
using Cluckwork.Application.Features.EggGrades.SetEggGradeActive;
using Cluckwork.Application.Features.EggGrades.UpdateEggGrade;
using Cluckwork.Application.Features.EggLots;
using Cluckwork.Application.Features.Flocks;
using Cluckwork.Application.Features.Flocks.ArchiveFlock;
using Cluckwork.Application.Features.Flocks.CreateFlock;
using Cluckwork.Application.Features.Flocks.DepleteFlock;
using Cluckwork.Application.Features.Flocks.ReactivateFlock;
using Cluckwork.Application.Features.Flocks.RecordBirdMovement;
using Cluckwork.Application.Features.Flocks.UpdateFlock;
using Cluckwork.Application.Features.Sales;
using Cluckwork.Application.Features.Sales.AddOrderItem;
using Cluckwork.Application.Features.Sales.CancelSalesOrder;
using Cluckwork.Application.Features.Sales.ConfirmSale;
using Cluckwork.Application.Features.Sales.CreateSalesOrder;
using Cluckwork.Application.Features.Sales.RemoveOrderItem;
using Cluckwork.Application.Features.Sales.UpdateOrderItem;
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
builder.Services.AddScoped<IEggGradeRepository, EggGradeRepository>();
builder.Services.AddScoped<ISalesOrderRepository, SalesOrderRepository>();
builder.Services.AddScoped<IFlockRepository, FlockRepository>();
builder.Services.AddScoped<IBirdMovementRepository, BirdMovementRepository>();
builder.Services.AddScoped<ICustomerRepository, CustomerRepository>();
builder.Services.AddScoped<IAccountRepository, AccountRepository>();

// --- Validators ---
builder.Services.AddScoped<IValidator<RecordDailyEntryCommand>, RecordDailyEntryValidator>();
builder.Services.AddScoped<IValidator<CreateFlockCommand>, CreateFlockValidator>();
builder.Services.AddScoped<IValidator<CreateCustomerCommand>, CreateCustomerValidator>();
builder.Services.AddScoped<IValidator<CreateSalesOrderCommand>, CreateSalesOrderValidator>();
builder.Services.AddScoped<IValidator<AddOrderItemCommand>, AddOrderItemValidator>();
builder.Services.AddScoped<IValidator<UpdateOrderItemCommand>, UpdateOrderItemValidator>();
builder.Services.AddScoped<IValidator<CreateEggGradeCommand>, CreateEggGradeValidator>();
builder.Services.AddScoped<IValidator<UpdateEggGradeCommand>, UpdateEggGradeValidator>();
builder.Services.AddScoped<IValidator<UpdateFlockCommand>, UpdateFlockValidator>();
builder.Services.AddScoped<IValidator<RecordBirdMovementCommand>, RecordBirdMovementValidator>();

// --- Handlers (direct — no mediator, tech spec §2.1) ---
builder.Services.AddScoped<RecordDailyEntryHandler>();
builder.Services.AddScoped<SubmitDailyEntryHandler>();
builder.Services.AddScoped<CreateCustomerHandler>();
builder.Services.AddScoped<CreateSalesOrderHandler>();
builder.Services.AddScoped<AddOrderItemHandler>();
builder.Services.AddScoped<CancelSalesOrderHandler>();
builder.Services.AddScoped<RemoveOrderItemHandler>();
builder.Services.AddScoped<UpdateOrderItemHandler>();
builder.Services.AddScoped<ConfirmSaleHandler>();
builder.Services.AddScoped<CreateFlockHandler>();
builder.Services.AddScoped<DepleteFlockHandler>();
builder.Services.AddScoped<CreateEggGradeHandler>();
builder.Services.AddScoped<UpdateEggGradeHandler>();
builder.Services.AddScoped<SetEggGradeActiveHandler>();
builder.Services.AddScoped<UpdateFlockHandler>();
builder.Services.AddScoped<ArchiveFlockHandler>();
builder.Services.AddScoped<RecordBirdMovementHandler>();
builder.Services.AddScoped<ReactivateFlockHandler>();

// --- Startup seed (single-farm MVP) ---
builder.Services.Configure<SeedOptions>(builder.Configuration.GetSection(SeedOptions.SectionName));
builder.Services.AddScoped<DatabaseSeeder>();
builder.Services.AddScoped<DemoDataSeeder>();

// --- OpenAPI ---
builder.Services.AddOpenApi();

// --- Health checks ---
builder.Services.AddHealthChecks();

// --- Durable job scaffold (tech spec §9) ---
builder.Services.AddHostedService<DurableJobWorker>();

// ----------------------------------------------------------------
var app = builder.Build();
// ----------------------------------------------------------------

// --- Startup: apply migrations, then seed (both idempotent) ---
// The two switches are independent: Database:MigrateOnStartup (default true)
// gates only the migration — useful when a deploy job runs migrations — while
// seeding always runs and self-gates on Seed:Enabled + supplied credentials
// (see DatabaseSeeder).
{
    using var startupScope = app.Services.CreateScope();
    var sp = startupScope.ServiceProvider;
    if (builder.Configuration.GetValue("Database:MigrateOnStartup", true))
        await sp.GetRequiredService<AppDbContext>().Database.MigrateAsync();
    await sp.GetRequiredService<DatabaseSeeder>().SeedAsync();
}

// Demo sample data (#58): own scope — it resolves the TenantContext to the
// seeded account, which must not leak into the scope above.
{
    using var demoScope = app.Services.CreateScope();
    await demoScope.ServiceProvider.GetRequiredService<DemoDataSeeder>().SeedAsync();
}

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

app.MapGroup("/api/v1/flocks")
    .WithTags("Flocks")
    .RequireAuthorization()
    .MapFlockEndpoints();

app.MapGroup("/api/v1/egg-grades")
    .WithTags("EggGrades")
    .RequireAuthorization()
    .MapEggGradeEndpoints();

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
    .MapCustomerEndpoints();

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
