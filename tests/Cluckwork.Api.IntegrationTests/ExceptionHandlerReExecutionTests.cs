namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Features.Expenses.CreateExpense;
using Cluckwork.Application.Features.Expenses.CreateExpenseCategory;
using Cluckwork.Application.Features.Users.ChangeOwnPassword;
using Cluckwork.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// The faults these tests need are injected as validator replacements, so the
// throw originates INSIDE the endpoint — downstream of IdempotencyMiddleware,
// exactly where a real handler fault would. Malformed JSON is deliberately NOT
// used to provoke them: minimal-API body-binding failures
// (RouteHandlerOptions.ThrowOnBadRequest) are not reproducible request-to-
// request under this TestServer, so a suite built on them flakes.
internal sealed class ThrowingChangeOwnPasswordValidator : AbstractValidator<ChangeOwnPasswordCommand>
{
    public ThrowingChangeOwnPasswordValidator() =>
        RuleFor(c => c.NewPassword).Custom((_, _) =>
            throw new InvalidOperationException("change-password fault"));
}

internal sealed class ThrowingCreateExpenseValidator : AbstractValidator<CreateExpenseCommand>
{
    public ThrowingCreateExpenseValidator() =>
        RuleFor(c => c.Description).Custom((_, _) =>
            throw new InvalidOperationException("create-expense fault"));
}

// The realistic shape of a handler fault: the DATABASE is what died, which is
// why the handler threw. Dropping the request's own DbConnection reproduces a
// real outage/failover — every subsequent command on this scope fails, the
// error path included.
internal sealed class DbOutageCategoryValidator : AbstractValidator<CreateExpenseCategoryCommand>
{
    public DbOutageCategoryValidator(AppDbContext db) =>
        RuleFor(c => c.Name).Custom((_, _) =>
        {
            db.Database.GetDbConnection().Dispose();
            throw new InvalidOperationException("database connection lost mid-request");
        });
}

public sealed class ExceptionReExecutionFactory : CluckworkWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.AddScoped<IValidator<ChangeOwnPasswordCommand>, ThrowingChangeOwnPasswordValidator>();
            services.AddScoped<IValidator<CreateExpenseCommand>, ThrowingCreateExpenseValidator>();
            services.AddScoped<IValidator<CreateExpenseCategoryCommand>, DbOutageCategoryValidator>();
        });
    }
}

// #345 — UseExceptionHandler(ExceptionHandlingPath = "/error") does not just
// render a body: it rewrites Request.Path to "/error" and RE-EXECUTES the whole
// downstream pipeline, IdempotencyMiddleware included, with the original
// request's verb, headers and (once TenantResolutionMiddleware re-runs) tenant
// still attached. Everything the middleware decides from Request.Path is then
// decided about a request the client never sent.
//
// These tests pin the outcomes MEASURED on the pre-fix main:
//   (a) an exempt auth route's real 500 was served as a 400 "Idempotency-Key
//       header is required";
//   (b) a plain tracked write was ALREADY correct (EndpointHash covers the path,
//       so the "/error" claim never collided with the original) — kept below as
//       a characterization test;
//   (c) but the re-execution provably DID re-enter the claim protocol, so an
//       "/error"-scoped claim row could replace a real error with a bogus 409;
//   (d) and, worst, rendering an error became a DATABASE WRITE — when the fault
//       IS the database the claim INSERT failed too, the exception handler
//       itself failed, and the client got no ProblemDetails at all.
[Collection(ExceptionReExecutionCollection.Name)]
public sealed class ExceptionHandlerReExecutionTests(ExceptionReExecutionFactory factory)
{
    private const string ProblemDetails500 = "https://tools.ietf.org/html/rfc9110#section-15.6.1";

    // Mirrors IdempotencyMiddleware's own key derivation so a test can address
    // the exact claim row the "/error" re-execution would use.
    private static string Sha256(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static object ExpenseBody(Guid categoryId) => new
    {
        expenseCategoryId = categoryId,
        date = DateOnly.FromDateTime(DateTime.UtcNow.Date),
        description = "Probe expense",
        amountMinorUnits = 5_00L,
        flockId = (Guid?)null,
        note = (string?)null
    };

    private async Task<(Guid AccountId, Guid CategoryId, HttpClient Client)> SeedAsync(string prefix)
    {
        var email = $"{prefix}-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var categoryId = Guid.NewGuid();
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            db.ExpenseCategories.Add(Cluckwork.Domain.Expenses.ExpenseCategory.Create(
                categoryId, accountId, Cluckwork.Domain.Accounts.SeedDefaults.FarmId, "Probe-Category"));
            await db.SaveChangesAsync();
        });
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        return (accountId, categoryId, client);
    }

    private static async Task<List<IdempotencyRecord>> ClaimsAsync(
        CluckworkWebApplicationFactory factory, Guid accountId) =>
        await factory.WithTenantScopeAsync(accountId, db =>
            db.IdempotencyRecords.AsNoTracking().Where(r => r.AccountId == accountId).ToListAsync());

    // (a) An idempotency-EXEMPT route (ResponseNotCacheable) that throws. It
    // correctly sends no Idempotency-Key; the exemption is matched on
    // Request.Path, which the re-execution has rewritten to "/error", so before
    // the fix the exemption stopped matching and this middleware answered with
    // its OWN 400 — burying a genuine 500 under a header-validation error that
    // would send a client off debugging its request instead of the server.
    [Fact]
    public async Task Exempt_auth_route_that_throws_returns_the_mapped_status_not_an_idempotency_400()
    {
        var (_, _, client) = await SeedAsync("exempt");

        // Generated at runtime, never a literal — and never actually applied: the
        // injected validator throws before the handler sees either value.
        var response = await client.PostAsJsonAsync("/api/v1/auth/change-password", new
        {
            currentPassword = TestHarness.Password,
            newPassword = $"{Guid.NewGuid():N}aA1!"
        });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains(ProblemDetails500, body, StringComparison.Ordinal);
        Assert.DoesNotContain("idempotency-key-required", body, StringComparison.Ordinal);
    }

    // (b) CHARACTERIZATION, not a mutant-killer: a plain idempotency-TRACKED
    // write whose handler throws was already correct before the fix, and stays
    // correct after. It is here because the opposite was the working hypothesis:
    // the claim key is (AccountId, EndpointHash, IdempotencyKeyHash) and
    // EndpointHash = sha256("{method}:{path}"), so the re-execution's "/error"
    // claim lands in a DIFFERENT key space and cannot collide with the original
    // request's own still-InProgress row (which the catch has already released
    // anyway). Reverting the fix leaves this test GREEN — deliberately.
    [Fact]
    public async Task Tracked_write_that_throws_returns_the_mapped_status_and_leaves_no_claim_behind()
    {
        var (accountId, categoryId, client) = await SeedAsync("tracked");

        var response = await client.PostWithKeyAsync(
            "/api/v1/expenses", Guid.NewGuid().ToString(), ExpenseBody(categoryId));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains(ProblemDetails500, body, StringComparison.Ordinal);
        Assert.Empty(await ClaimsAsync(factory, accountId));
    }

    // (c) Proof that the re-execution really did re-enter the claim protocol —
    // and that doing so lets state keyed on the SYNTHETIC "/error" path decide
    // what a client is told about a completely different request. A row that
    // only a POST:/error claim under this key could ever match, carrying a
    // deliberately mismatching RequestHash, turned the real 500 into a 409
    // "Idempotency-Key was already used with a different request". Reachable in
    // production from concurrent same-key failures; the point here is that no
    // "/error"-scoped row should exist to be consulted at all.
    [Fact]
    public async Task Error_scoped_claim_row_cannot_hijack_a_failing_requests_response()
    {
        var (accountId, categoryId, client) = await SeedAsync("hijack");
        var key = Guid.NewGuid().ToString();

        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            db.IdempotencyRecords.Add(new IdempotencyRecord
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                EndpointHash = Sha256("POST:/error"),
                IdempotencyKeyHash = Sha256(key),
                RequestHash = new string('a', 64), // never equal to a real body hash
                Status = IdempotencyStatus.InProgress,
                LeaseOwner = Guid.NewGuid(),
                LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10), // a LIVE lease
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        });

        var response = await client.PostWithKeyAsync("/api/v1/expenses", key, ExpenseBody(categoryId));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains(ProblemDetails500, body, StringComparison.Ordinal);
        Assert.DoesNotContain("idempotency-key-conflict", body, StringComparison.Ordinal);
    }

    // (d) The consequence that actually matters in production. The database is
    // the reason the handler threw, so the error path must not need the database
    // to render an answer. Before the fix the re-execution's claim INSERT hit the
    // same dead connection, the exception handler itself failed, ASP.NET rethrew
    // the ORIGINAL exception, and the client got no ProblemDetails at all (here,
    // an exception straight out of HttpClient.SendAsync).
    [Fact]
    public async Task Database_fault_still_renders_problem_details_instead_of_escaping()
    {
        var (accountId, _, client) = await SeedAsync("outage");

        var response = await client.PostWithKeyAsync(
            "/api/v1/expense-categories", Guid.NewGuid().ToString(), new { name = "Outage probe" });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains(ProblemDetails500, body, StringComparison.Ordinal);

        // The ORIGINAL pass owns the claim, and its best-effort release runs on
        // the same dead connection — so its row survives as InProgress. That is
        // pre-existing and unchanged by this fix: it is exactly the "claimant
        // died mid-request" state the lease exists for, so it self-heals on
        // expiry via the steal path (AtomicIdempotencyProtocolTests covers that
        // recovery). Asserted here so the leftover is a KNOWN bounded row scoped
        // to the real endpoint, never an unbounded or "/error"-scoped one.
        var stuck = Assert.Single(await ClaimsAsync(factory, accountId));
        Assert.Equal(IdempotencyStatus.InProgress, stuck.Status);
        Assert.Equal(Sha256("POST:/api/v1/expense-categories"), stuck.EndpointHash);
        Assert.True(stuck.LeaseExpiresAt < DateTimeOffset.UtcNow.AddMinutes(5),
            $"lease must be bounded so the claim self-heals; got {stuck.LeaseExpiresAt:O}");
    }
}

// Own collection (not "integration"): the throwing validators above replace real
// ones process-wide for this factory, so this class needs its own host.
[CollectionDefinition(Name)]
public sealed class ExceptionReExecutionCollection : ICollectionFixture<ExceptionReExecutionFactory>
{
    public const string Name = "exception-reexecution";
}
