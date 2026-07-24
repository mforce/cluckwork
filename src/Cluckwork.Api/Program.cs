using System.Security.Cryptography;
using Cluckwork.Api;
using Cluckwork.Api.Configuration;
using Microsoft.Extensions.Options;
using Cluckwork.Api.Endpoints.Accounts;
using Cluckwork.Api.Endpoints.Audit;
using Cluckwork.Api.Endpoints.Auth;
using Cluckwork.Api.Endpoints.Catalog;
using Cluckwork.Api.Endpoints.Customers;
using Cluckwork.Api.Endpoints.DailyEntries;
using Cluckwork.Api.Endpoints.EggGrades;
using Cluckwork.Api.Endpoints.Expenses;
using Cluckwork.Api.Endpoints.Export;
using Cluckwork.Api.Endpoints.Flocks;
using Cluckwork.Api.Endpoints.Reports;
using Cluckwork.Api.Endpoints.Inventory;
using Cluckwork.Api.Endpoints.Sales;
using Cluckwork.Api.Endpoints.Water;
using Cluckwork.Api.Endpoints.Stock;
using Cluckwork.Api.Endpoints.Users;
using Cluckwork.Api.Hosting;
using Cluckwork.Api.Middleware;
using Cluckwork.Api.RateLimiting;
using Cluckwork.Api.Security;
using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Accounts;
using Cluckwork.Application.Features.Customers;
using Cluckwork.Application.Features.Customers.CreateCustomer;
using Cluckwork.Application.Features.DailyEntries;
using Cluckwork.Application.Features.DailyEntries.AdjustDailyEntry;
using Cluckwork.Application.Features.DailyEntries.RecordDailyEntry;
using Cluckwork.Application.Features.DailyEntries.SubmitDailyEntry;
using Cluckwork.Application.Features.DailyEntries.VoidDailyEntry;
using Cluckwork.Application.Features.EggGrades;
using Cluckwork.Application.Features.Inventory;
using Cluckwork.Application.Features.Inventory.CreateInventoryItem;
using Cluckwork.Application.Features.Inventory.RecordAdjustment;
using Cluckwork.Application.Features.Inventory.RecordFeedUsage;
using Cluckwork.Application.Features.Inventory.RecordPurchase;
using Cluckwork.Application.Features.Inventory.RecordWaterUsage;
using Cluckwork.Application.Features.Inventory.UpdateWaterUsage;
using Cluckwork.Application.Features.Inventory.UpdateInventoryItem;
using Cluckwork.Application.Features.EggGrades.CreateEggGrade;
using Cluckwork.Application.Features.EggGrades.SetEggGradeActive;
using Cluckwork.Application.Features.EggGrades.UpdateEggGrade;
using Cluckwork.Application.Features.Expenses;
using Cluckwork.Application.Features.Expenses.AdjustExpense;
using Cluckwork.Application.Features.Expenses.CreateExpense;
using Cluckwork.Application.Features.Expenses.CreateExpenseCategory;
using Cluckwork.Application.Features.Expenses.UpdateExpenseCategory;
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
using Cluckwork.Application.Features.Sales.RecordPayment;
using Cluckwork.Application.Features.Sales.RemoveOrderItem;
using Cluckwork.Application.Features.Sales.UpdateOrderItem;
using Cluckwork.Application.Features.Sales.VoidPayment;
using Cluckwork.Application.Features.Sales.VoidSale;
using Cluckwork.Application.Features.Users.CreateUser;
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
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using System.Globalization;
using System.Threading.RateLimiting;

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
// The acting user, resolved beside the tenant (#93 audit trail).
builder.Services.AddScoped<Cluckwork.Infrastructure.Identity.CurrentUserContext>();
builder.Services.AddScoped<Cluckwork.Application.Common.ICurrentUser>(sp =>
    sp.GetRequiredService<Cluckwork.Infrastructure.Identity.CurrentUserContext>());
builder.Services.AddScoped<Cluckwork.Application.Common.IAuditWriter, AuditWriter>();
builder.Services.AddScoped<Cluckwork.Application.Features.Audit.IAuditEventRepository, AuditEventRepository>();
builder.Services.AddScoped<Cluckwork.Application.Features.Eggs.IEggInventoryMovementRepository, EggInventoryMovementRepository>();
builder.Services.AddScoped<Cluckwork.Application.Features.Users.IUserRoleAssignmentRepository, UserRoleAssignmentRepository>();
builder.Services.AddScoped<Cluckwork.Application.Common.IFlockScopeGuard, FlockScopeGuard>();
builder.Services.AddScoped<Cluckwork.Application.Features.Users.AssignFlock.AssignFlockHandler>();
builder.Services.AddScoped<Cluckwork.Application.Features.Users.AssignFlock.UnassignFlockHandler>();
builder.Services.AddScoped<Cluckwork.Application.Features.Export.IExportQueries, ExportQueries>();

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
        // Per-account lockout (#128): 5 failures locks the account for a 15-minute
        // cool-off (matching the #143 per-IP login window). Enforced in
        // IdentityProvider.LoginAsync; CreateAsync enables lockout on new users
        // via AllowedForNewUsers (default true).
        opts.Lockout.MaxFailedAccessAttempts = 5;
        opts.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    })
    .AddRoles<ApplicationRole>()
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders();

// --- Auth rate limiting (#143) ---
// Per-client-IP fixed windows on the anonymous auth endpoints. The real client
// IP is resolved by the framework ForwardedHeaders middleware below (not by
// hand-parsing X-Forwarded-For); this only buckets by it. Config is validated
// eagerly so a bad value fails at boot, not on the first login request.
var rateLimiting = builder.Configuration.GetSection(RateLimitingOptions.SectionName)
    .Get<RateLimitingOptions>() ?? new RateLimitingOptions();
rateLimiting.Validate();
var trustedProxies = rateLimiting.ParseTrustedProxies();

// X-Forwarded-For (the client IP for the limiter) and X-Forwarded-Proto (the
// real scheme, so HttpsRedirection/HSTS behave behind the proxy — #144). Both
// are honoured only from the trusted proxy networks below.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Bound the trusted hop chain by network membership, not a fixed count.
    options.ForwardLimit = null;
    // Replace the framework defaults (which trust loopback) with exactly the
    // configured proxy networks — an untrusted peer's XFF is then ignored.
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
    foreach (var network in trustedProxies)
        options.KnownIPNetworks.Add(network);
});

// HSTS (#144) — emitted outside Development once the forwarded proto tells the
// app the request is really HTTPS. A year, subdomains included.
builder.Services.AddHsts(options =>
{
    options.MaxAge = TimeSpan.FromDays(365);
    options.IncludeSubDomains = true;
});

// Host pinning (#144): the framework host-filtering middleware reads the
// `AllowedHosts` config value; deployments set it to the public hostname so a
// forged Host header is rejected (400). Loopback is force-added whenever a
// specific host is pinned, so in-container health probes (Host: localhost) keep
// working no matter what the operator configured. A "*" value disables pinning.
builder.Services.PostConfigure<HostFilteringOptions>(options =>
{
    if (options.AllowedHosts.Contains("*")) return;
    // Config binds AllowedHosts as a fixed-size array; replace it with a list
    // that also admits loopback, rather than mutating the array in place.
    var hosts = new List<string>(options.AllowedHosts);
    foreach (var loopback in new[] { "localhost", "127.0.0.1", "[::1]" })
        if (!hosts.Contains(loopback))
            hosts.Add(loopback);
    options.AllowedHosts = hosts;
});

builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.OnRejected = static async (context, ct) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        await Results.Problem(
                title: "Too many requests",
                detail: "Too many authentication attempts from this address. Try again later.",
                statusCode: StatusCodes.Status429TooManyRequests)
            .ExecuteAsync(context.HttpContext);
    };
    AddFixedWindowByClientIp(limiter, RateLimitingOptions.LoginPolicyName, rateLimiting.Login);
    AddFixedWindowByClientIp(limiter, RateLimitingOptions.RefreshPolicyName, rateLimiting.Refresh);

    static void AddFixedWindowByClientIp(
        Microsoft.AspNetCore.RateLimiting.RateLimiterOptions limiter,
        string policyName, RateLimitingOptions.FixedWindow window) =>
        limiter.AddPolicy(policyName, context =>
            RateLimitPartition.GetFixedWindowLimiter(
                RateLimitKey.ForClient(context.Connection.RemoteIpAddress),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = window.PermitLimit,
                    Window = TimeSpan.FromSeconds(window.WindowSeconds),
                    QueueLimit = 0
                }));
});

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

        // Keep the raw JWT claim names ("role", "sub", "account_id") instead of
        // remapping to the legacy XML-schema claim types — the SPA decodes the
        // same short names from the token payload (#73).
        opts.MapInboundClaims = false;
        opts.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidateAudience = true,
            ValidAudience = builder.Configuration["Jwt:Audience"],
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new RsaSecurityKey(rsa),
            ClockSkew = TimeSpan.FromSeconds(30),
            RoleClaimType = "role"
        };
    });

// #73 — Admin vs not-Admin only; house/flock-scoped RBAC is a later slice.
builder.Services.AddAuthorization(opts => opts.AddCluckworkPolicies());
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler,
    ForbiddenProblemResultHandler>();

// --- Application ports ---
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddScoped<IClock, SystemClock>();
// #35: farm-local date boundary, shared by the stock read, sale allocation and
// the future-date validators. Scoped so one request resolves the tenant's
// timezone once and every boundary in it agrees.
builder.Services.AddScoped<IFarmClock, FarmClock>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IIdentityProvider, IdentityProvider>();
builder.Services.AddScoped<IJwtTokenService, JwtTokenService>();
builder.Services.AddScoped<IDailyEntryRepository, DailyEntryRepository>();
builder.Services.AddScoped<IEggLotRepository, EggLotRepository>();
builder.Services.AddScoped<IEggGradeRepository, EggGradeRepository>();
builder.Services.AddScoped<Cluckwork.Application.Features.Catalog.IProductRepository, ProductRepository>();
builder.Services.AddScoped<Cluckwork.Application.Features.Catalog.IEggUnitConversionRepository, EggUnitConversionRepository>();
builder.Services.AddScoped<IExpenseCategoryRepository, ExpenseCategoryRepository>();
builder.Services.AddScoped<IExpenseRepository, ExpenseRepository>();
builder.Services.AddScoped<ISalesOrderRepository, SalesOrderRepository>();
builder.Services.AddScoped<ISalesOrderAllocationRepository, SalesOrderAllocationRepository>();
builder.Services.AddScoped<IPaymentRepository, PaymentRepository>();
builder.Services.AddScoped<Cluckwork.Application.Features.Reports.IReportQueries, ReportQueries>();
builder.Services.AddScoped<IInventoryItemRepository, InventoryItemRepository>();
builder.Services.AddScoped<IInventoryLotRepository, InventoryLotRepository>();
builder.Services.AddScoped<IInventoryMovementRepository, InventoryMovementRepository>();
builder.Services.AddScoped<IFeedUsageRepository, FeedUsageRepository>();
builder.Services.AddScoped<IWaterUsageRepository, WaterUsageRepository>();
builder.Services.AddScoped<IFlockRepository, FlockRepository>();
builder.Services.AddScoped<IBirdMovementRepository, BirdMovementRepository>();
builder.Services.AddScoped<ICustomerRepository, CustomerRepository>();
builder.Services.AddScoped<IAccountRepository, AccountRepository>();
builder.Services.AddScoped<Cluckwork.Application.Features.Accounts.ICurrencyBoundRowProbe, CurrencyBoundRowProbe>();
builder.Services.AddScoped<Cluckwork.Application.Features.Accounts.IFarmLogoRepository, FarmLogoRepository>();

// --- Validators ---
builder.Services.AddScoped<IValidator<RecordDailyEntryCommand>, RecordDailyEntryValidator>();
builder.Services.AddScoped<IValidator<Cluckwork.Application.Features.Catalog.CreateProduct.CreateProductCommand>,
    Cluckwork.Application.Features.Catalog.CreateProduct.CreateProductValidator>();
builder.Services.AddScoped<IValidator<Cluckwork.Application.Features.Catalog.UpdateProduct.UpdateProductCommand>,
    Cluckwork.Application.Features.Catalog.UpdateProduct.UpdateProductValidator>();
builder.Services.AddScoped<IValidator<Cluckwork.Application.Features.Catalog.UpdateEggUnitConversion.UpdateEggUnitConversionCommand>,
    Cluckwork.Application.Features.Catalog.UpdateEggUnitConversion.UpdateEggUnitConversionValidator>();
builder.Services.AddScoped<IValidator<CreateFlockCommand>, CreateFlockValidator>();
builder.Services.AddScoped<IValidator<CreateCustomerCommand>, CreateCustomerValidator>();
builder.Services.AddScoped<IValidator<CreateSalesOrderCommand>, CreateSalesOrderValidator>();
builder.Services.AddScoped<IValidator<AddOrderItemCommand>, AddOrderItemValidator>();
builder.Services.AddScoped<IValidator<UpdateOrderItemCommand>, UpdateOrderItemValidator>();
builder.Services.AddScoped<IValidator<CreateEggGradeCommand>, CreateEggGradeValidator>();
builder.Services.AddScoped<IValidator<UpdateEggGradeCommand>, UpdateEggGradeValidator>();
builder.Services.AddScoped<IValidator<CreateExpenseCategoryCommand>, CreateExpenseCategoryValidator>();
builder.Services.AddScoped<IValidator<UpdateExpenseCategoryCommand>, UpdateExpenseCategoryValidator>();
builder.Services.AddScoped<IValidator<CreateExpenseCommand>, CreateExpenseValidator>();
builder.Services.AddScoped<IValidator<AdjustExpenseCommand>, AdjustExpenseValidator>();
builder.Services.AddScoped<IValidator<UpdateFlockCommand>, UpdateFlockValidator>();
builder.Services.AddScoped<IValidator<RecordBirdMovementCommand>, RecordBirdMovementValidator>();
builder.Services.AddScoped<IValidator<VoidSaleCommand>, VoidSaleValidator>();
builder.Services.AddScoped<IValidator<RecordPaymentCommand>, RecordPaymentValidator>();
builder.Services.AddScoped<IValidator<VoidPaymentCommand>, VoidPaymentValidator>();
builder.Services.AddScoped<IValidator<CreateInventoryItemCommand>, CreateInventoryItemValidator>();
builder.Services.AddScoped<IValidator<UpdateInventoryItemCommand>, UpdateInventoryItemValidator>();
builder.Services.AddScoped<IValidator<RecordPurchaseCommand>, RecordPurchaseValidator>();
builder.Services.AddScoped<IValidator<RecordFeedUsageCommand>, RecordFeedUsageValidator>();
builder.Services.AddScoped<IValidator<RecordAdjustmentCommand>, RecordAdjustmentValidator>();
builder.Services.AddScoped<IValidator<RecordWaterUsageCommand>, RecordWaterUsageValidator>();
builder.Services.AddScoped<IValidator<UpdateWaterUsageCommand>, UpdateWaterUsageValidator>();
builder.Services.AddScoped<IValidator<CreateUserCommand>, CreateUserValidator>();
builder.Services.AddScoped<IValidator<AdjustDailyEntryCommand>, AdjustDailyEntryValidator>();
builder.Services.AddScoped<IValidator<VoidDailyEntryCommand>, VoidDailyEntryValidator>();
builder.Services.AddScoped<
    IValidator<Cluckwork.Application.Features.Accounts.UpdateFarmSettings.UpdateFarmSettingsCommand>,
    Cluckwork.Application.Features.Accounts.UpdateFarmSettings.UpdateFarmSettingsValidator>();

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
builder.Services.AddScoped<VoidSaleHandler>();
builder.Services.AddScoped<RecordPaymentHandler>();
builder.Services.AddScoped<VoidPaymentHandler>();
builder.Services.AddScoped<CreateInventoryItemHandler>();
builder.Services.AddScoped<UpdateInventoryItemHandler>();
builder.Services.AddScoped<SetInventoryItemActiveHandler>();
builder.Services.AddScoped<RecordPurchaseHandler>();
builder.Services.AddScoped<RecordFeedUsageHandler>();
builder.Services.AddScoped<RecordAdjustmentHandler>();
builder.Services.AddScoped<RecordWaterUsageHandler>();
builder.Services.AddScoped<UpdateWaterUsageHandler>();
builder.Services.AddScoped<CreateFlockHandler>();
builder.Services.AddScoped<DepleteFlockHandler>();
builder.Services.AddScoped<CreateEggGradeHandler>();
builder.Services.AddScoped<Cluckwork.Application.Features.Catalog.CreateProduct.CreateProductHandler>();
builder.Services.AddScoped<Cluckwork.Application.Features.Catalog.UpdateProduct.UpdateProductHandler>();
builder.Services.AddScoped<Cluckwork.Application.Features.Catalog.SetProductActive.SetProductActiveHandler>();
builder.Services.AddScoped<Cluckwork.Application.Features.Catalog.UpdateEggUnitConversion.UpdateEggUnitConversionHandler>();
builder.Services.AddScoped<UpdateEggGradeHandler>();
builder.Services.AddScoped<SetEggGradeActiveHandler>();
builder.Services.AddScoped<CreateExpenseCategoryHandler>();
builder.Services.AddScoped<UpdateExpenseCategoryHandler>();
builder.Services.AddScoped<CreateExpenseHandler>();
builder.Services.AddScoped<AdjustExpenseHandler>();
builder.Services.AddScoped<UpdateFlockHandler>();
builder.Services.AddScoped<ArchiveFlockHandler>();
builder.Services.AddScoped<RecordBirdMovementHandler>();
builder.Services.AddScoped<ReactivateFlockHandler>();
builder.Services.AddScoped<CreateUserHandler>();
builder.Services.AddScoped<AdjustDailyEntryHandler>();
builder.Services.AddScoped<
    Cluckwork.Application.Features.Accounts.UpdateFarmSettings.UpdateFarmSettingsHandler>();
builder.Services.AddScoped<
    Cluckwork.Application.Features.Accounts.SetFarmLogo.SetFarmLogoHandler>();
builder.Services.AddScoped<
    Cluckwork.Application.Features.Accounts.RemoveFarmLogo.RemoveFarmLogoHandler>();
builder.Services.AddScoped<VoidDailyEntryHandler>();

// --- Farm logo upload cap (#123): operational limit under the domain ceiling,
// validated at startup so a value above the ceiling fails the boot, not the
// first upload.
builder.Services.AddOptions<FarmLogoOptions>()
    .Bind(builder.Configuration.GetSection(FarmLogoOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<FarmLogoOptions>, FarmLogoOptionsValidator>();

// --- Startup seed (single-farm MVP) ---
builder.Services.Configure<SeedOptions>(builder.Configuration.GetSection(SeedOptions.SectionName));
builder.Services.AddScoped<DatabaseSeeder>();
builder.Services.AddScoped<DemoDataSeeder>();

// --- OpenAPI ---
builder.Services.AddOpenApi();

// --- Health checks ---
// Readiness includes the database (#65): during a DB outage — or with
// migrations pending — the API stays up (liveness green) while /health/ready
// turns 503 so orchestrators stop routing traffic until it recovers. The job
// worker reports via a heartbeat: a stall shows as Degraded (still HTTP 200 —
// a dead background job must not pull API traffic).
builder.Services.AddSingleton<DurableJobWorkerHeartbeat>();
builder.Services.AddHealthChecks()
    .AddCheck<Cluckwork.Api.HealthChecks.DatabaseReadyHealthCheck>("database")
    .AddCheck<Cluckwork.Api.HealthChecks.DurableJobWorkerHealthCheck>("durable-job-worker");

// --- Durable job scaffold (tech spec §9) + recurring sweeps ---
builder.Services.AddSingleton<DailyEntryLockSweep>();
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

// Resolve the real client IP and scheme from a trusted proxy's forwarded
// headers before anything reads RemoteIpAddress or Request.Scheme (the rate
// limiter, HTTPS redirection and HSTS all depend on it) — #143/#144.
app.UseForwardedHeaders();

// Security response headers on every response (#144). Outermost after the
// forwarded headers so it also covers static files and error responses.
app.UseSecurityHeaders();

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

// Endpoint rate-limit policies (#143) — no global limiter, only routes that
// opt in via RequireRateLimiting are affected.
app.UseRateLimiter();

app.UseAuthentication();
app.UseMiddleware<TenantResolutionMiddleware>();
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

// Exposes Program for WebApplicationFactory in integration tests
public partial class Program { }
