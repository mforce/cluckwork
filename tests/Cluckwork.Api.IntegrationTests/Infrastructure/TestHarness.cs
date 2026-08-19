namespace Cluckwork.Api.IntegrationTests.Infrastructure;

using System.Linq;
using System.Net.Http.Headers;
using Cluckwork.Api.Endpoints.Auth;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Eggs;
using Cluckwork.Domain.Sales;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

// Seeding + auth helpers shared by the integration tests. Each helper that touches the
// database opens its own DI scope with a resolved TenantContext, mirroring how the
// tenant middleware resolves the account per request in production.
internal static class TestHarness
{
    public const string Password = "TestPassw0rd!23";

    // Creates an Account row + an ApplicationUser bound to it, returning the account id.
    // Users are Admin by default — #73 gated the corrective endpoints, and most tests
    // exercise them; pass asAdmin: false for a worker.
    public static async Task<Guid> SeedAccountWithUserAsync(
        this CluckworkWebApplicationFactory factory, string email, bool asAdmin = true,
        string timeZoneId = "UTC")
    {
        var accountId = Guid.NewGuid();

        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            db.Accounts.Add(Account.Create(accountId, "Test Farm Co", "farm-" + accountId.ToString("N")[..12], timeZoneId, "USD"));
            // Every account carries the packed-unit defaults (#97) — mirrors the
            // startup seeder's SeedDefaultEggUnitConversionsAsync.
            db.EggUnitConversions.AddRange(
                Cluckwork.Domain.Catalog.EggUnitConversion.Defaults(accountId));
            await db.SaveChangesAsync();
        });

        await factory.SeedUserAsync(accountId, email, asAdmin);
        return accountId;
    }

    // Adds another user to an existing account (e.g. a worker beside the admin).
    public static Task SeedUserAsync(
        this CluckworkWebApplicationFactory factory, Guid accountId, string email, bool asAdmin) =>
        factory.SeedUserAsync(accountId, email,
            asAdmin ? Cluckwork.Domain.Accounts.Roles.Owner : null);

    // #103 — seed a user with any role (null = plain worker).
    public static async Task SeedUserAsync(
        this CluckworkWebApplicationFactory factory, Guid accountId, string email, string? role)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = email,
            Email = email,
            AccountId = accountId
        };
        var result = await users.CreateAsync(user, Password);
        if (!result.Succeeded)
            throw new InvalidOperationException(
                "Seed user creation failed: " + string.Join("; ", result.Errors.Select(e => e.Description)));

        if (role is not null)
        {
            // The startup seeder doesn't run in tests (Seed:Enabled unset) — create
            // the role on first use. Tests in the collection run sequentially, so
            // the exists-then-create pair doesn't race.
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
            if (!await roles.RoleExistsAsync(role))
                await roles.CreateAsync(new ApplicationRole { Id = Guid.NewGuid(), Name = role });

            var added = await users.AddToRoleAsync(user, role);
            if (!added.Succeeded)
                throw new InvalidOperationException(
                    "Seed role assignment failed: " + string.Join("; ", added.Errors.Select(e => e.Description)));
        }
    }

    // #283 — seed a user with MustChangePassword=true, exactly the shape
    // `bootstrap-admin` produces for the first-run Owner. Separate helper
    // (rather than another SeedUserAsync overload) because this is a rare,
    // deliberately-flagged shape, not a general seeding knob.
    public static async Task<Guid> SeedUserPendingPasswordChangeAsync(
        this CluckworkWebApplicationFactory factory, Guid accountId, string email, string? role = Cluckwork.Domain.Accounts.Roles.Owner)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = email,
            Email = email,
            AccountId = accountId,
            MustChangePassword = true,
        };
        var result = await users.CreateAsync(user, Password);
        if (!result.Succeeded)
            throw new InvalidOperationException(
                "Seed user creation failed: " + string.Join("; ", result.Errors.Select(e => e.Description)));

        if (role is not null)
        {
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
            if (!await roles.RoleExistsAsync(role))
                await roles.CreateAsync(new ApplicationRole { Id = Guid.NewGuid(), Name = role });
            var added = await users.AddToRoleAsync(user, role);
            if (!added.Succeeded)
                throw new InvalidOperationException(
                    "Seed role assignment failed: " + string.Join("; ", added.Errors.Select(e => e.Description)));
        }

        return user.Id;
    }

    // #104 — pile a second role onto an existing user (multi-role principals
    // are reachable via Identity even though the API assigns one; the policy
    // precedence tests need them).
    public static async Task AddRoleAsync(
        this CluckworkWebApplicationFactory factory, string email, string role)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        if (!await roles.RoleExistsAsync(role))
            await roles.CreateAsync(new ApplicationRole { Id = Guid.NewGuid(), Name = role });
        var user = await users.FindByEmailAsync(email)
            ?? throw new InvalidOperationException($"No user {email}");
        var added = await users.AddToRoleAsync(user, role);
        if (!added.Succeeded)
            throw new InvalidOperationException(
                "AddRole failed: " + string.Join("; ", added.Errors.Select(e => e.Description)));
    }

    // Opens a scope, resolves the tenant to accountId, and hands the AppDbContext to the
    // caller. The query filter and tenant-stamp interceptor then behave exactly as in a
    // real request for that account.
    public static async Task WithTenantScopeAsync(
        this CluckworkWebApplicationFactory factory, Guid accountId, Func<AppDbContext, Task> action)
    {
        using var scope = factory.Services.CreateScope();
        var tenant = scope.ServiceProvider.GetRequiredService<TenantContext>();
        tenant.Resolve(accountId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await action(db);
    }

    public static async Task<T> WithTenantScopeAsync<T>(
        this CluckworkWebApplicationFactory factory, Guid accountId, Func<AppDbContext, Task<T>> action)
    {
        using var scope = factory.Services.CreateScope();
        var tenant = scope.ServiceProvider.GetRequiredService<TenantContext>();
        tenant.Resolve(accountId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await action(db);
    }

    // #500 — resolves BOTH the tenant and the acting user on a hand-built scope.
    //
    // The two helpers above hand back a raw AppDbContext for direct EF writes,
    // which never reach IAuditWriter — they need no actor and are unaffected.
    // This one is for the race tests that build their own scope and then invoke
    // an auditing HANDLER: since #500 IAuditWriter fails closed on an
    // unresolved actor, so tenant-only is no longer enough.
    //
    // Pass the acting user where the test has one, so the audit row names the
    // same person the handler was told is acting. Where it has none, a fresh id
    // stands in: any resolved actor satisfies the guard, and an actor with no
    // UserRoleAssignment rows is account-wide as far as FlockScopeGuard is
    // concerned, so this cannot quietly narrow what the test could do before.
    //
    // `roles` defaults to Owner because every current caller drives a handler
    // that never consults ICurrentUser.Roles. It is a PARAMETER rather than a
    // constant because the default is not inert everywhere: a future race test
    // over a flock-scoped handler (RecordDailyEntry, SubmitDailyEntry,
    // RecordFeedUsage, RecordWaterUsage) would get FlockScopeGuard's Owner
    // bypass whatever role its acting user actually holds in the database — and
    // would then pass while proving nothing about the authorization it meant to
    // exercise. Such a test must pass the real roles (#500 mid-point review).
    public static IServiceScope ResolveTenantAndActor(
        this IServiceScope scope, Guid accountId, Guid? actorId = null, string? actorEmail = null,
        IReadOnlyList<string>? roles = null)
    {
        scope.ServiceProvider.GetRequiredService<TenantContext>().Resolve(accountId);
        var id = actorId ?? Guid.NewGuid();
        scope.ServiceProvider.GetRequiredService<CurrentUserContext>()
            .Resolve(id, actorEmail ?? $"actor-{id:N}@test.local", roles ?? [Roles.Owner]);
        return scope;
    }

    // Seeds saleable egg grades for the account + farm; returns name -> id.
    // Grades are farm-scoped (spec §9.1) — pass the same farmId the test posts.
    public static async Task<Dictionary<string, Guid>> SeedEggGradesAsync(
        this CluckworkWebApplicationFactory factory, Guid accountId, Guid farmId, params string[] names)
    {
        var ids = new Dictionary<string, Guid>();
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var sort = 0;
            foreach (var name in names)
            {
                var grade = EggGrade.Create(
                    Guid.NewGuid(), accountId, farmId,
                    name, EggGradeType.Size, sort++, isSaleable: true);
                db.EggGrades.Add(grade);
                ids[name] = grade.Id;
            }
            await db.SaveChangesAsync();
        });
        return ids;
    }

    // Seeds an egg product mapped to a grade (#99 — sales lines sell products).
    // Unit Egg → factor 1, so quantities behave exactly like the old raw-grade
    // lines unless a test opts into packed units.
    public static async Task<Guid> SeedProductAsync(
        this CluckworkWebApplicationFactory factory, Guid accountId, Guid farmId,
        Guid eggGradeId, string? name = null, long? defaultPriceMinorUnits = null)
    {
        var productId = Guid.NewGuid();
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            db.Products.Add(Cluckwork.Domain.Catalog.Product.Create(
                productId, accountId, farmId,
                name ?? $"Product-{productId.ToString()[..8]}",
                Cluckwork.Domain.Catalog.ProductType.Egg,
                Cluckwork.Domain.Catalog.ProductUnit.Egg,
                defaultPriceMinorUnits, "USD", 2, notes: null));
            db.ProductEggGradeMappings.Add(Cluckwork.Domain.Catalog.ProductEggGradeMapping.Create(
                Guid.NewGuid(), accountId, productId, eggGradeId));
            await db.SaveChangesAsync();
        });
        return productId;
    }

    // Seeds an Active flock — daily entries now require a live flock row (#47),
    // so tests can no longer post entries against invented flock ids.
    public static async Task<Guid> SeedFlockAsync(
        this CluckworkWebApplicationFactory factory, Guid accountId, Guid farmId, Guid? houseId = null)
    {
        var flockId = Guid.NewGuid();
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            db.Flocks.Add(Cluckwork.Domain.Flocks.Flock.Create(
                flockId, accountId, farmId, houseId ?? Guid.NewGuid(),
                $"Flock-{flockId.ToString()[..8]}", "Test Breed",
                DateOnly.FromDateTime(DateTime.UtcNow.Date).AddDays(-30), initialCount: 100));
            await db.SaveChangesAsync();
        });
        return flockId;
    }

    // Seeds an egg lot for the account. Pass restrictedUntil to make it withdrawal-restricted.
    // eggGradeId must reference a seeded EggGrade row (see SeedEggGradesAsync) — lots FK to grades.
    public static async Task<Guid> SeedEggLotAsync(
        this CluckworkWebApplicationFactory factory, Guid accountId,
        Guid eggGradeId, int quantity, DateOnly? restrictedUntil = null,
        DateOnly? productionDate = null)
    {
        var lotId = Guid.NewGuid();
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var lot = EggLot.Create(lotId, accountId, Guid.NewGuid(),
                productionDate ?? DateOnly.FromDateTime(DateTime.UtcNow.Date), eggGradeId, quantity);
            if (restrictedUntil is not null)
                lot.SetWithdrawalRestriction(restrictedUntil.Value);
            // Seeded lots keep the #101 ledger invariant: their opening
            // balance exists as an explicit Production movement, exactly as
            // the real submit path writes it.
            db.EggInventoryMovements.Add(Cluckwork.Domain.Eggs.EggInventoryMovement.Create(
                Guid.NewGuid(), accountId, lotId, Cluckwork.Domain.Eggs.EggMovementType.Production,
                quantity, "DailyEntry", Guid.NewGuid(), DateTimeOffset.UtcNow));
            db.EggLots.Add(lot);
            await db.SaveChangesAsync();
        });
        return lotId;
    }

    // Seeds a draft sales order with a single line item for the given grade/quantity.
    public static Task<Guid> SeedSalesOrderAsync(
        this CluckworkWebApplicationFactory factory, Guid accountId,
        Guid eggGradeId, int quantity) =>
        factory.SeedSalesOrderAsync(accountId, [(eggGradeId, quantity)]);

    // Multi-line variant (one line per grade/quantity pair).
    public static async Task<Guid> SeedSalesOrderAsync(
        this CluckworkWebApplicationFactory factory, Guid accountId,
        IReadOnlyList<(Guid EggGradeId, int Quantity)> lines)
    {
        var orderId = Guid.NewGuid();
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            // Orders FK to Customers — seed a real customer row.
            var customer = Customer.Create(Guid.NewGuid(), accountId, "Seed Customer", "555-0000");
            db.Customers.Add(customer);

            var order = SalesOrder.Create(
                orderId, accountId, customer.Id,
                $"SO-{orderId.ToString()[..8]}", DateOnly.FromDateTime(DateTime.UtcNow.Date), "USD");
            foreach (var (eggGradeId, quantity) in lines)
            {
                // #99: lines carry a product; seed one per grade line (unit Egg,
                // factor 1 — quantities unchanged).
                var product = Cluckwork.Domain.Catalog.Product.Create(
                    Guid.NewGuid(), accountId, Guid.NewGuid(),
                    $"P-{Guid.NewGuid():N}"[..20],
                    Cluckwork.Domain.Catalog.ProductType.Egg,
                    Cluckwork.Domain.Catalog.ProductUnit.Egg, null, "USD", 2, null);
                db.Products.Add(product);
                db.ProductEggGradeMappings.Add(Cluckwork.Domain.Catalog.ProductEggGradeMapping.Create(
                    Guid.NewGuid(), accountId, product.Id, eggGradeId));
                order.AddItem(product.Id, Cluckwork.Domain.Catalog.ProductType.Egg, eggGradeId,
                    Cluckwork.Domain.Catalog.ProductUnit.Egg, 1, quantity,
                    Cluckwork.Domain.Common.Money.Zero("USD"));
            }
            db.SalesOrders.Add(order);
            await db.SaveChangesAsync();
        });
        return orderId;
    }

    // Cookies are managed explicitly in these tests (login reads the Set-Cookie,
    // refresh/logout send it back by hand), so the client must NOT keep its own
    // jar — otherwise an explicit Cookie header collides with the container.
    //
    // The BaseAddress must be copied off the factory rather than left at the
    // WebApplicationFactoryClientOptions default: only the PARAMETERLESS
    // CreateClient() rewrites the base address to the bound Kestrel port under
    // UseKestrel(0), so a client built from an options object would otherwise
    // point at http://localhost/ and reach nothing. Under the in-memory
    // TestServer (every other suite) the factory's ClientOptions.BaseAddress is
    // that same default, so this changes nothing there.
    private static WebApplicationFactoryClientOptions Cookieless(CluckworkWebApplicationFactory factory) =>
        new() { HandleCookies = false, BaseAddress = factory.ClientOptions.BaseAddress };

    // #532 — the farm code of the account InitialCreate seeds and AddAccountSlug
    // backfills. Use it ONLY for a login whose email belongs to no account (the
    // "unknown user" negative paths): a valid farm code plus an unknown email is
    // what still produces Identity.InvalidCredentials rather than
    // Auth.UnknownFarmCode, which is what those tests assert.
    public const string DefaultFarmCode = "default-farm";

    // #532 — for every OTHER login, resolve the farm code of the account the user
    // actually belongs to. SeedAccountWithUserAsync mints a FRESH account per test
    // with slug "farm-<guid12>", so most of this suite never touches the default
    // farm; hardcoding one code would 401 them all with Auth.UnknownFarmCode.
    // Doing the lookup here rather than threading a slug through every caller is
    // what keeps the ~18 LoginAsync call sites unchanged.
    public static async Task<string> FarmCodeForAsync(
        this CluckworkWebApplicationFactory factory, string email)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var normalizer = scope.ServiceProvider.GetRequiredService<ILookupNormalizer>();
        var normalized = normalizer.NormalizeEmail(email);

        // Test-only, and deliberately global: the point is to discover WHICH farm
        // the seeded user is in. IgnoreQueryFilters because Accounts is
        // tenant-filtered and this scope resolves no tenant.
        // #532 review — the whole point of this slice is that an email CAN now
        // belong to several farms, so FirstOrDefault would silently pick
        // whichever row Postgres ordered first and hide the ambiguity a test
        // ought to state. Fail loudly instead: a test with a shared email must
        // pass its farm code explicitly.
        var accountIds = await db.Users
            .Where(u => u.NormalizedEmail == normalized)
            .Select(u => u.AccountId)
            .ToListAsync();
        if (accountIds.Count > 1)
            throw new InvalidOperationException(
                $"'{email}' exists in {accountIds.Count} accounts, so its farm code is ambiguous. "
                + "Pass the farm code explicitly in this test instead of resolving it from the email.");
        var accountId = accountIds.Count == 1 ? accountIds[0] : Guid.Empty;

        return accountId == Guid.Empty
            ? DefaultFarmCode
            : await db.Accounts.IgnoreQueryFilters()
                .Where(a => a.Id == accountId)
                .Select(a => a.Slug)
                .FirstAsync();
    }

    // Logs in over HTTP; the access token comes from the body and the refresh
    // token from the HttpOnly Set-Cookie (#145).
    public static async Task<TokenPairDto> LoginAsync(
        this CluckworkWebApplicationFactory factory, string email)
    {
        var client = factory.CreateClient(Cookieless(factory));
        var farmCode = await factory.FarmCodeForAsync(email);
        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login", new { farmCode, email, password = Password });
        response.EnsureSuccessStatusCode();
        return await ReadTokensAsync(response);
    }

    public static async Task<string> LoginForAccessTokenAsync(
        this CluckworkWebApplicationFactory factory, string email) =>
        (await factory.LoginAsync(email)).AccessToken;

    public static HttpClient CreateAuthedClient(
        this CluckworkWebApplicationFactory factory, string accessToken)
    {
        var client = factory.CreateClient(Cookieless(factory));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    // Reads an AccessTokenResponse body + the rotated refresh cookie into the
    // TokenPairDto shape the auth tests assert against.
    public static async Task<TokenPairDto> ReadTokensAsync(HttpResponseMessage response)
    {
        var body = (await response.Content.ReadFromJsonAsync<AccessTokenResponse>())!;
        return new TokenPairDto(body.AccessToken, ExtractRefreshCookie(response), body.AccessTokenExpiry);
    }

    // Pulls the refresh-token value out of the Set-Cookie header (empty when the
    // cookie is being cleared, e.g. logout / failed refresh).
    public static string ExtractRefreshCookie(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var cookies))
            return string.Empty;
        var prefix = AuthCookies.RefreshCookieName + "=";
        var cookie = cookies.FirstOrDefault(c => c.StartsWith(prefix, StringComparison.Ordinal));
        if (cookie is null) return string.Empty;
        return cookie[prefix.Length..].Split(';', 2)[0];
    }

    // POST /auth/refresh the cookie way: refresh token in the cookie, the #145
    // CSRF header present. Pass csrf: false to exercise the missing-header path.
    public static Task<HttpResponseMessage> PostRefreshAsync(
        this HttpClient client, string? refreshToken, bool csrf = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        if (csrf) request.Headers.Add(AuthCookies.CsrfHeaderName, "1");
        if (refreshToken is not null)
            request.Headers.Add("Cookie", $"{AuthCookies.RefreshCookieName}={refreshToken}");
        return client.SendAsync(request);
    }

    // POST /auth/logout the SPA way (#145): cookie-authenticated — the refresh
    // cookie + CSRF header, and never an Idempotency-Key (the route is exempt).
    //
    // #336 — accessToken sends the bearer the SPA now attaches when the tab
    // still holds one. The two credentials are independent on purpose, so a
    // test can present a cookie and a bearer naming DIFFERENT users (the
    // per-origin cookie vs. per-tab token split), or a bearer with no cookie
    // at all. Left null it behaves exactly as before.
    public static Task<HttpResponseMessage> PostLogoutAsync(
        this HttpClient client, string? refreshToken, bool csrf = true, string? accessToken = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        if (csrf) request.Headers.Add(AuthCookies.CsrfHeaderName, "1");
        if (refreshToken is not null)
            request.Headers.Add("Cookie", $"{AuthCookies.RefreshCookieName}={refreshToken}");
        if (accessToken is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client.SendAsync(request);
    }

    // POST with the Idempotency-Key write endpoints require. Optional JSON body.
    public static Task<HttpResponseMessage> PostWithKeyAsync(
        this HttpClient client, string url, string idempotencyKey, object? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return client.SendAsync(request);
    }

    // PUT with the Idempotency-Key write endpoints require (same as POST).
    public static Task<HttpResponseMessage> PutWithKeyAsync(
        this HttpClient client, string url, string idempotencyKey, object? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, url);
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return client.SendAsync(request);
    }

    // #165 — log in with an EXPLICIT password, returning the raw response so a
    // test can assert both that a new password works and that the old one 401s.
    public static async Task<HttpResponseMessage> TryLoginAsync(
        this CluckworkWebApplicationFactory factory, string email, string password)
    {
        var farmCode = await factory.FarmCodeForAsync(email);
        return await factory.CreateClient(Cookieless(factory)).PostAsJsonAsync(
            "/api/v1/auth/login", new { farmCode, email, password });
    }

    // --- Row-lock observation (#162, #313) ---
    //
    // Whether a competing request PARKED on a row lock is an observable fact,
    // not a timing guess: a row-lock wait registers the holder's backend in
    // pg_blocking_pids. Two opposite guarantees are asserted through these —
    // #162 wants the competitor to block (the protocol serialized the sides),
    // #313 wants it never to (a foreign tenant's row is filtered out before
    // FOR UPDATE is attempted) — so the probe lives here rather than being
    // copied per test class, where a later fix to one copy would silently
    // leave the other behind.

    private static readonly TimeSpan LockWaitDeadline = TimeSpan.FromSeconds(15);

    public static Task<int> BackendPidAsync(this AppDbContext db) =>
        db.Database.SqlQuery<int>($"""SELECT pg_backend_pid() AS "Value" """).SingleAsync();

    // Count of backends currently blocked (directly or transitively) behind
    // holderPid. Keying on the holder's pid (rather than grepping query text,
    // where a parameterized account id never appears) keeps this immune to
    // other test classes touching the same tables on their own rows.
    //
    // #402 — pg_blocking_pids(pid) reports only the NEAREST blocker in the
    // wait queue, not the ultimate lock holder: a second competitor queued
    // behind a FIRST one (itself queued behind holderPid) reports the first
    // competitor's pid, never holderPid's, even though it is transitively
    // waiting on holderPid to release. Confirmed empirically: with dbA
    // (holderPid) fencing a writer, then a change queuing behind the WRITER
    // (not directly behind dbA), the change reports blockedby={writerPid},
    // never {..., holderPid}. A plain `@> ARRAY[holderPid]` filter therefore
    // undercounts the moment more than one competitor queues up — see
    // minBlockedCount below for why that matters. The recursive walk here
    // follows the chain to find every backend for which holderPid appears
    // ANYWHERE in its (possibly multi-hop) blocking chain.
    public static async Task<int> CountBlockedBehindAsync(
        this CluckworkWebApplicationFactory factory, int holderPid)
    {
        var count = 0;
        await factory.WithTenantScopeAsync(Guid.NewGuid(), async db =>
        {
            count = (int)await db.Database.SqlQuery<long>($"""
                WITH RECURSIVE chain(pid, blocker) AS (
                    SELECT pid, unnest(pg_blocking_pids(pid)) FROM pg_stat_activity
                    WHERE pid != pg_backend_pid()
                    UNION
                    SELECT chain.pid, unnest(pg_blocking_pids(chain.blocker))
                    FROM chain
                )
                SELECT count(DISTINCT pid) AS "Value" FROM chain WHERE blocker = {holderPid}
                """).SingleAsync();
        });
        return count;
    }

    // True while ANY backend sits blocked on a lock held by holderPid. A
    // caller that needs "THIS specific request blocked" and is the ONLY one
    // contending with holderPid can rely on this — true for every #313
    // negative-assertion caller (a lone competitor, checked once). A caller
    // racing a SECOND competitor behind an ALREADY-blocked first one must NOT
    // use this: it goes true on the very first poll purely because the first
    // competitor is still parked, without ever proving the second one joined
    // the queue at all. Use WaitUntilDoneOrBlockedAsync's minBlockedCount for
    // that shape instead (#402).
    public static async Task<bool> AnyoneBlockedBehindAsync(
        this CluckworkWebApplicationFactory factory, int holderPid) =>
        await factory.CountBlockedBehindAsync(holderPid) > 0;

    // Polls until the competing task either finishes without contending
    // (false) or provably parks behind the holder's lock, with AT LEAST
    // minBlockedCount backends registered as waiters (true).
    //
    // #402 — minBlockedCount defaults to 1 (a lone competitor; every existing
    // caller before this fix). A caller that already proved a FIRST
    // competitor parked and now needs to prove a SECOND one ALSO parked
    // behind the SAME holder — to pin their relative queue order — must pass
    // minBlockedCount: 2. Checking merely "count > 0" there is satisfied
    // immediately by the first competitor still sitting blocked, before the
    // second one's lock request has even reached Postgres; a fence released
    // right after that vacuous true can hand the row to whichever of the two
    // actually arrives first, not whichever asked first. Requiring the count
    // to reach 2 forces this call to observe the second competitor's own
    // registration in pg_stat_activity before it returns, so releasing the
    // fence afterward only ever wakes an already-fully-queued, FIFO-ordered
    // pair.
    public static async Task<bool> WaitUntilDoneOrBlockedAsync(
        this CluckworkWebApplicationFactory factory, Task competing, int holderPid,
        int minBlockedCount = 1)
    {
        var stopAt = DateTime.UtcNow + LockWaitDeadline;
        while (DateTime.UtcNow < stopAt)
        {
            if (competing.IsCompleted) return false;
            if (await factory.CountBlockedBehindAsync(holderPid) >= minBlockedCount) return true;
            await Task.Delay(50);
        }
        throw new TimeoutException("Neither completion nor a lock wait was observed.");
    }
}

public sealed record TokenPairDto(string AccessToken, string RefreshToken, DateTimeOffset AccessTokenExpiry);
