namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using Cluckwork.Api.Endpoints.Auth;
using Cluckwork.Application.Common;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// #532 — AccountSuspensionService. The service has NO caller in this slice (#534
// ships the operator verbs), so these tests are the only thing that exercises it
// and they invoke it directly out of DI.
//
// The guarantee worth testing is not "IsActive goes false" — a boolean-only
// implementation (IsActive flipped, no epoch bump, no revocation) passes most of
// this file, and the tests that pin each specific guarantee are:
//   * the middleware GATE (the AccountIsActive clause in the per-request read)
//     → ABearerWhoseEpochStillMatches_IsRejected_WhenTheFarmIsInactive
//   * the suspended-farm check in RefreshAsync
//     → ARefreshTokenWhoseEpochStillMatches_IsRejected_WhenTheFarmIsInactive
//       (this one only reaches that check since the refresh token is decoded
//       with Uri.UnescapeDataString: the raw cookie value is percent-encoded,
//       so an encoded token hashed to nothing and RefreshAsync failed at its
//       FIRST branch — InvalidRefreshToken — before the suspended-farm check
//       ever ran. Round 5 proved the guard was deletable with the whole suite
//       green on that account; the decode is what makes this test genuine.)
//   * reactivation's revoke (nothing survives a suspend/reactivate cycle)
//     → ReactivationRevokesTheSessionsMintedBetweenSuspendAndReactivate
//   * account scoping (the ExecuteUpdateAsync pair is scoped by AccountId and
//     does not sweep the whole users table)
//     → SuspendAsync_LeavesAnotherFarmUntouched
// Round-3 review proved the first three guards were ALL simultaneously
// deletable with this suite green: every test suspended through
// AccountSuspensionService, which also bumps the epoch, so the epoch clause
// rejected the request and the account clause never had to work.
// DeactivateWithoutEpochBumpAsync below is the fixture that breaks that
// coupling.
[Collection(IntegrationCollection.Name)]
public sealed class AccountSuspensionTests(CluckworkWebApplicationFactory factory)
{
    private sealed record UserState(int CredentialEpoch, string? SecurityStamp, string? ConcurrencyStamp);

    // One scope per call: AccountSuspensionService resolves the tenant itself and
    // TenantContext is single-assignment (#546), so a shared scope throws on the
    // second call.
    private async Task SuspendAsync(Guid accountId)
    {
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<AccountSuspensionService>();
        var result = await service.SuspendAsync(accountId, reason: null);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Description : "");
    }

    private async Task ReactivateAsync(Guid accountId)
    {
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<AccountSuspensionService>();
        var result = await service.ReactivateAsync(accountId, reason: null);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Description : "");
    }

    private async Task<UserState> ReadUserAsync(string email)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Email == email);
        return new UserState(user.CredentialEpoch, user.SecurityStamp, user.ConcurrencyStamp);
    }

    private async Task<int> LiveRefreshTokenCountAsync(Guid accountId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.RefreshTokens.AsNoTracking()
            .CountAsync(t => t.AccountId == accountId && t.RevokedAt == null);
    }

    // Flips IsActive WITHOUT touching CredentialEpoch. Every other test in this
    // file suspends through AccountSuspensionService, which also bumps the epoch —
    // so the epoch clause rejects the request and the account clause never has to
    // work. That coupling is why three guards were simultaneously deletable with
    // this suite green (round-3 review). This is the state a bearer minted in the
    // same instant as the suspension commit would face.
    private Task DeactivateWithoutEpochBumpAsync(Guid accountId) =>
        factory.WithTenantScopeAsync(accountId, db =>
            db.Accounts.Where(a => a.Id == accountId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(a => a.IsActive, false)));

    private Task<bool> IsActiveAsync(Guid accountId) =>
        factory.WithTenantScopeAsync(accountId, db =>
            db.Accounts.AsNoTracking().Where(a => a.Id == accountId).Select(a => a.IsActive).SingleAsync());

    private async Task<HttpResponseMessage> TryLoginAsync(string farmCode, string email)
    {
        var client = factory.CreateClient();
        return await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { farmCode, email, password = TestHarness.Password });
    }

    [Fact]
    public async Task SuspendAsync_BumpsEveryEpoch_RotatesBothStamps_AndRevokesRefreshTokens()
    {
        var owner = $"susp-owner-{Guid.NewGuid():N}@test.local";
        var worker = $"susp-worker-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(owner);
        await factory.SeedUserAsync(accountId, worker, asAdmin: false);

        // Two live sessions, so the revocation assertion below covers more than
        // the single row a one-user farm would produce.
        _ = await factory.LoginAsync(owner);
        _ = await factory.LoginAsync(worker);
        Assert.Equal(2, await LiveRefreshTokenCountAsync(accountId));

        var ownerBefore = await ReadUserAsync(owner);
        var workerBefore = await ReadUserAsync(worker);

        await SuspendAsync(accountId);

        Assert.False(await IsActiveAsync(accountId));

        var ownerAfter = await ReadUserAsync(owner);
        var workerAfter = await ReadUserAsync(worker);

        // Epoch +1 exactly — not merely "changed". An implementation that reset it
        // to a constant would kill today's tokens and silently unkill tomorrow's.
        Assert.Equal(ownerBefore.CredentialEpoch + 1, ownerAfter.CredentialEpoch);
        Assert.Equal(workerBefore.CredentialEpoch + 1, workerAfter.CredentialEpoch);

        // SecurityStamp kills outstanding STEP-UP grants, which bind to the stamp
        // and never to CredentialEpoch.
        Assert.NotEqual(ownerBefore.SecurityStamp, ownerAfter.SecurityStamp);
        Assert.NotEqual(workerBefore.SecurityStamp, workerAfter.SecurityStamp);

        // ConcurrencyStamp is the fence: without rotating it, a concurrent
        // same-user Identity write that read the row BEFORE this transaction still
        // matches on UpdateAsync and writes its stale CredentialEpoch back.
        Assert.NotEqual(ownerBefore.ConcurrencyStamp, ownerAfter.ConcurrencyStamp);
        Assert.NotEqual(workerBefore.ConcurrencyStamp, workerAfter.ConcurrencyStamp);

        Assert.Equal(0, await LiveRefreshTokenCountAsync(accountId));
    }

    // #532 round-5 — the revocation sweep stamps RevokedAt from the TimeProvider
    // read, not from the row's CreatedAt or a constant. Pin the exact instant by
    // driving the service with the harness FakeTimeProvider: a mutation that
    // changed which clock (or which value) feeds RevokedAt would land a
    // different instant here and redden.
    [Fact]
    public async Task SuspendAsync_StampsRevokedAtFromTheTimeProviderRead()
    {
        var email = $"susp-revokeat-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        _ = await factory.LoginAsync(email);
        Assert.Equal(1, await LiveRefreshTokenCountAsync(accountId));

        var clock = new Cluckwork.Api.IntegrationTests.SharedState.FakeTimeProvider();
        clock.Advance(TimeSpan.FromDays(3)); // a distinctive, non-"now" instant

        // Resolve the collaborators from ONE scope so they share the transaction
        // and tenant, and pass the FakeTimeProvider in place of the DI singleton.
        using var scope = factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var service = new AccountSuspensionService(
            sp.GetRequiredService<AppDbContext>(),
            sp.GetRequiredService<TenantContext>(),
            sp.GetRequiredService<Cluckwork.Application.Features.Accounts.IAccountRepository>(),
            clock,
            sp.GetRequiredService<Cluckwork.Application.Common.IAuditWriter>(),
            sp.GetRequiredService<CurrentUserContext>());

        var result = await service.SuspendAsync(accountId, reason: null);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Description : "");

        using (var checkScope = factory.Services.CreateScope())
        {
            var db = checkScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.RefreshTokens.AsNoTracking()
                .SingleAsync(t => t.AccountId == accountId && t.RevokedAt != null);
            Assert.Equal(clock.GetUtcNow(), row.RevokedAt);
        }
    }

    // #532 round-5 — the revocation sweep filters on `RevokedAt == null`, so an
    // already-revoked row must NOT be touched. Dropping that predicate would
    // re-stamp the existing RevokedAt (overwriting the original revocation
    // instant) on every suspend/reactivate. Pin it with an already-revoked row
    // whose stamp must survive the sweep untouched.
    [Fact]
    public async Task SuspendAsync_DoesNotOverwriteAnAlreadyRevokedRefreshRow()
    {
        var email = $"susp-alreadyrev-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        _ = await factory.LoginAsync(email);
        Assert.Equal(1, await LiveRefreshTokenCountAsync(accountId));

        // A second row that is ALREADY revoked, stamped with a distinctive
        // instant. The sweep must leave this exact instant in place.
        var preRevokedStamp = new DateTimeOffset(2020, 5, 5, 5, 5, 5, TimeSpan.Zero);
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var userId = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
            db.RefreshTokens.Add(new RefreshToken
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                AccountId = accountId,
                TokenHash = Guid.NewGuid().ToString("N"),
                CreatedAt = preRevokedStamp,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
                IssuedEpoch = 0,
                RevokedAt = preRevokedStamp,
            });
            await db.SaveChangesAsync();
        });

        await SuspendAsync(accountId);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.RefreshTokens.AsNoTracking()
            .SingleAsync(t => t.AccountId == accountId && t.RevokedAt == preRevokedStamp);
        // The sweep must have left the pre-existing revocation stamp untouched —
        // not overwritten with the TimeProvider instant of the suspend.
        Assert.Equal(preRevokedStamp, row.RevokedAt);
    }

    // #579 — premise 3's atomicity guard. The suspension's same-transaction
    // revocation is one of the four pinned premises of the won't-fix decision
    // (docs/decisions/579-suspension-issuance-window.md); the existing
    // SuspendAsync_BumpsEveryEpoch… test only observes the successful final
    // state, so a refactor that splits the transaction stays green on it.
    //
    // A split-FlushChanges (commit the account row first, then the sweep) is
    // invisible to ANY runtime test: it still commits in one Postgres
    // transaction, so a fault at the audit write rolls back everything either
    // way. Verified by mutation on this slice — the mutation stayed green on
    // the runtime assertions. The guard that catches it is therefore the
    // static one (below): no SaveChanges of any kind before the final flush.
    //
    // The runtime half is still worth keeping, because it pins the ROLLBACK
    // path the static check cannot see: a fault at the audit write (the last
    // in-transaction write before the final SaveChanges) must leave the
    // account active, every epoch/stamp unchanged, the token live, and the
    // audit row absent.
    [Fact]
    public async Task SuspendAsync_SuspendedPremiseIsAtomic_RollbacksWithTheEpochBump()
    {
        var email = $"susp-atomic-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        _ = await factory.LoginAsync(email);
        Assert.Equal(1, await LiveRefreshTokenCountAsync(accountId));

        var before = await ReadUserAsync(email);

        using var scope = factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();

        // The fault lands on the audit write, the last in-transaction write
        // before the final SaveChanges. With the user sweep's ExecuteUpdate
        // already executed (in-flight inside the transaction) and the account
        // row still unflushed, the rollback below has to undo both an executed
        // UPDATE and a tracked mutation — and the assertions say what "undone"
        // means for each.
        var faultingAudit = new FaultingOnFirstWriteAuditWriter(sp.GetRequiredService<Cluckwork.Application.Common.IAuditWriter>());

        var service = new AccountSuspensionService(
            db,
            sp.GetRequiredService<TenantContext>(),
            sp.GetRequiredService<Cluckwork.Application.Features.Accounts.IAccountRepository>(),
            sp.GetRequiredService<TimeProvider>(),
            faultingAudit,
            sp.GetRequiredService<CurrentUserContext>());

        // The service has no catch for an unexpected fault (it returns failure
        // Results for expected domain errors only), so our fault propagates —
        // and the rollback below has to run.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SuspendAsync(accountId, reason: "atomicity guard"));

        // Nothing of the suspension may have survived the rollback.
        Assert.True(await IsActiveAsync(accountId));
        var after = await ReadUserAsync(email);
        Assert.Equal(before, after);
        Assert.Equal(1, await LiveRefreshTokenCountAsync(accountId));

        // The audit row itself rolled back with it — the row landed or not at
        // all, never alone.
        var auditRows = await factory.WithTenantScopeAsync(accountId, async d =>
            await d.AuditEvents.AsNoTracking()
                .CountAsync(e => e.Action == "Account.Suspend"));
        Assert.Equal(0, auditRows);

        // Static half of the guard: the TRANSACTION BOUNDARY. The regression
        // it exists to catch is the two-transaction variant — commit the
        // account row in one transaction, run the epoch/stamp sweep in a
        // second — which leaves IsActive false with live credentials and is
        // invisible to the runtime half: a fault at the audit write rolls back
        // whatever transaction it is in, and the successful-path assertions
        // observe only the final state. A flush COUNT cannot catch it either
        // (the file would still carry exactly one SaveChangesAsync) — that is
        // why this check is positional, not a count: the sweep and the flush
        // must both sit between BeginAsync and CommitAsync. Verified by
        // mutation on this slice (the two-transaction mutation reddens the
        // sweep-position assertion; the flush-count predecessor let it pass).
        // Same repo-root walk as ServingGuardCoverageTests.RepositoryRoot et al.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Cluckwork.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var serviceSource = await File.ReadAllTextAsync(
            Path.Combine(dir!.FullName, "src", "Cluckwork.Infrastructure", "Identity", "AccountSuspensionService.cs"));
        // Find every SaveChanges call site. The needle includes the receiver
        // dot: the file carries "SaveChanges" in a COMMENT (line 193, "before
        // The comment above ("before SaveChanges, so the row lands") is not a
        // flush; "SaveChangesAsync(" is the real call shape in this file. If a
        // future call renames the receiver, widen the needle in the same change.
        const string flushNeedle = "SaveChangesAsync(";
        // The epoch/stamp sweep is raw SQL, not a SaveChanges call: it is the
        // ExecuteUpdateAsync on Users inside the transaction. The file carries
        // TWO ExecuteUpdateAsync calls (the user sweep and the refresh-token
        // sweep); the premise-3 anchor is the USER sweep specifically, so the
        // needle includes the "db.Users" receiver. A generic "ExecuteUpdateAsync"
        // would match whichever appears first — if the user sweep moves after
        // the commit while the refresh sweep stays inside, a first-match on the
        // refresh sweep passes the boundary check with premise 3 broken. If the
        // user sweep moves to a different mechanism (e.g. a tracked update +
        // SaveChanges), this needle is stale in the other direction — update it
        // with the change. Premise 3 revokes BOTH in the same transaction, so a
        // matching db.RefreshTokens anchor is checked below too (moving only the
        // token sweep after the commit leaves the user sweep + flush in-boundary
        // green while token revocation is broken).
        const string sweepNeedle = "db.Users";
        const string sweepMethod = "ExecuteUpdateAsync";
        // The refresh-token sweep is the SECOND ExecuteUpdateAsync (on
        // RefreshTokens, revoking every live row). Premise 3 names BOTH
        // revocations in the same transaction as IsActive, so the guard must
        // bound this one too — a refactor moving only the token sweep after the
        // commit leaves the user sweep + flush in-boundary (green on the checks
        // below) with the token revocation broken.
        const string tokenSweepNeedle = "db.RefreshTokens";
        // The boundary is a TRANSACTION, not a flush count. A flush-count check
        // (its predecessor) is gameable: commit the account in one transaction,
        // run the sweep in a second, and the file still carries exactly one
        // SaveChangesAsync — green, while IsActive is false with live
        // credentials. The check that actually pins the premise is therefore
        // positional: the sweep must execute AFTER the transaction BEGIN and
        // BEFORE its COMMIT. A sweep outside that span is, by construction, in
        // a different transaction than the account row.
        var begin = serviceSource.IndexOf("AmbientTransaction.RunAsync", StringComparison.Ordinal);
        var commit = serviceSource.IndexOf("transaction.CommitAsync", StringComparison.Ordinal);
        var sweep = serviceSource.IndexOf(sweepNeedle, StringComparison.Ordinal);
        // Anchor on the user sweep specifically: find the db.Users receiver,
        // then confirm an ExecuteUpdateAsync follows it within the same
        // statement. The statement spans multiple lines (Where, ExecuteUpdate,
        // setters, token) so the window is generous; a bare db.Users elsewhere
        // (a query, a CountAsync) would not carry an ExecuteUpdateAsync within
        // this range.
        const int sweepWindow = 400;
        var sweepIsUpdate = sweep >= 0 &&
            serviceSource.Substring(sweep, Math.Min(sweepWindow, serviceSource.Length - sweep)).Contains(sweepMethod, StringComparison.Ordinal);
        var tokenSweep = serviceSource.IndexOf(tokenSweepNeedle, StringComparison.Ordinal);
        var tokenSweepIsUpdate = tokenSweep >= 0 &&
            serviceSource.Substring(tokenSweep, Math.Min(sweepWindow, serviceSource.Length - tokenSweep)).Contains(sweepMethod, StringComparison.Ordinal);
        var flush = serviceSource.IndexOf(flushNeedle, StringComparison.Ordinal);
        Assert.True(begin >= 0, "#579 premise 3: AmbientTransaction.BeginAsync not found — the guard's anchor moved; update the needle.");
        Assert.True(commit > begin, "#579 premise 3: CommitAsync before BeginAsync — the guard's anchors moved; update the needles.");
        Assert.True(sweepIsUpdate, "#579 premise 3: the user epoch/stamp sweep (db.Users … ExecuteUpdateAsync) not found — it moved to a different mechanism; update the guard in the same change.");
        Assert.True(tokenSweepIsUpdate, "#579 premise 3: the refresh-token sweep (db.RefreshTokens … ExecuteUpdateAsync) not found — it moved to a different mechanism; update the guard in the same change.");
        Assert.True(flush > 0, "#579 premise 3: SaveChangesAsync not found — the final flush moved; update the guard in the same change.");
        Assert.True(sweep > begin && sweep < commit,
            "#579 premise 3: the epoch/stamp sweep is outside the suspension transaction (between BeginAsync and CommitAsync). " +
            "Premise 3 of docs/decisions/579-suspension-issuance-window.md is one Postgres transaction around IsActive and the sweep; " +
            "if the sweep must leave the transaction, the premise is broken and #579 reopens — do not move the needle to make this green.");
        Assert.True(tokenSweep > begin && tokenSweep < commit,
            "#579 premise 3: the refresh-token sweep is outside the suspension transaction (between BeginAsync and CommitAsync). " +
            "Premise 3 revokes the farm's refresh tokens in the same transaction as IsActive; " +
            "if the token sweep must leave the transaction, the premise is broken and #579 reopens — do not move the needle to make this green.");
        Assert.True(flush > begin && flush < commit,
            "#579 premise 3: the final SaveChangesAsync is outside the suspension transaction. " +
            "If the account row commits in a different transaction than the sweep, IsActive can be false with live credentials — " +
            "that is exactly the regression this guard exists to catch.");
    }

    // #579 — test double for the atomicity guard above: throws on the FIRST
    // WriteAsync, the last write the suspension performs inside its
    // transaction.
    private sealed class FaultingOnFirstWriteAuditWriter(Cluckwork.Application.Common.IAuditWriter inner) : Cluckwork.Application.Common.IAuditWriter
    {
        public Task WriteAsync(string action, string entityType, Guid entityId, string? reason = null, object? details = null, CancellationToken ct = default)
        {
            _ = inner; // unused: the fault lands before any delegate call
            throw new InvalidOperationException("#579 atomicity guard: faulting on the audit write.");
        }
    }

    [Fact]
    public async Task SuspendedFarm_RejectsLogin_WithFarmSuspended()
    {
        var email = $"susp-login-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var farmCode = await factory.FarmCodeForAsync(email);

        Assert.Equal(HttpStatusCode.OK, (await TryLoginAsync(farmCode, email)).StatusCode);

        await SuspendAsync(accountId);

        var response = await TryLoginAsync(farmCode, email);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal(AuthEndpoints.FarmSuspendedCode, problem!.Title);
    }

    [Fact]
    public async Task SuspendedFarm_KillsAnInFlightBearer_OnTheNextRequest()
    {
        var email = $"susp-bearer-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);

        // Minted BEFORE the suspension and still well inside its 15-minute
        // lifetime: this is the bypass the whole slice exists to close.
        var tokens = await factory.LoginAsync(email);
        var client = factory.CreateAuthedClient(tokens.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/users")).StatusCode);

        await SuspendAsync(accountId);

        var response = await client.GetAsync("/api/v1/users");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // Auth.FarmSuspended, NOT Auth.CredentialsSuperseded. Suspension bumps the
        // epoch too, so this bearer fails both tests; asserting the title is what
        // pins the middleware's precedence (suspended farm BEFORE epoch). Telling
        // someone to "sign in again" when their sign-in cannot succeed is the bug.
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal(AuthEndpoints.FarmSuspendedCode, problem!.Title);
    }

    [Fact]
    public async Task ReactivateAsync_BringsTheFarmBack_ButPreSuspensionSessionsStayDead()
    {
        var email = $"susp-cycle-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var farmCode = await factory.FarmCodeForAsync(email);

        var tokens = await factory.LoginAsync(email);
        var staleClient = factory.CreateAuthedClient(tokens.AccessToken);

        await SuspendAsync(accountId);
        await ReactivateAsync(accountId);

        Assert.True(await IsActiveAsync(accountId));

        // The farm works again.
        Assert.Equal(HttpStatusCode.OK, (await TryLoginAsync(farmCode, email)).StatusCode);

        // But nothing minted before the suspension survives the cycle. This is the
        // assertion a boolean-only implementation fails: with IsActive back to
        // true and no epoch bump / no revocation, both of these would succeed.
        Assert.Equal(HttpStatusCode.Unauthorized, (await staleClient.GetAsync("/api/v1/users")).StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var identity = scope.ServiceProvider.GetRequiredService<IIdentityProvider>();
            // Uri.UnescapeDataString for the same reason as
            // ARefreshTokenWhoseEpochStillMatches…: the cookie value is
            // percent-encoded and RefreshAsync hashes whatever it is given.
            Assert.True((await identity.RefreshAsync(tokens.RefreshTokenForDirectCall)).IsFailure,
                "a refresh token minted before the suspension must not survive reactivation");
        }
    }

    [Fact]
    public async Task SuspendAsync_LeavesAnotherFarmUntouched()
    {
        var victimEmail = $"susp-a-{Guid.NewGuid():N}@test.local";
        var bystanderEmail = $"susp-b-{Guid.NewGuid():N}@test.local";
        var victimAccountId = await factory.SeedAccountWithUserAsync(victimEmail);
        var bystanderAccountId = await factory.SeedAccountWithUserAsync(bystanderEmail);
        var bystanderFarmCode = await factory.FarmCodeForAsync(bystanderEmail);

        _ = await factory.LoginAsync(victimEmail);
        var bystanderTokens = await factory.LoginAsync(bystanderEmail);
        var bystanderClient = factory.CreateAuthedClient(bystanderTokens.AccessToken);
        var bystanderBefore = await ReadUserAsync(bystanderEmail);

        await SuspendAsync(victimAccountId);

        // Both ExecuteUpdateAsync statements are scoped by AccountId. Drop either
        // WHERE clause and this test goes red while every other test in the file
        // still passes.
        Assert.True(await IsActiveAsync(bystanderAccountId));
        var bystanderAfter = await ReadUserAsync(bystanderEmail);
        Assert.Equal(bystanderBefore.CredentialEpoch, bystanderAfter.CredentialEpoch);
        Assert.Equal(bystanderBefore.SecurityStamp, bystanderAfter.SecurityStamp);
        Assert.Equal(1, await LiveRefreshTokenCountAsync(bystanderAccountId));
        Assert.Equal(HttpStatusCode.OK, (await bystanderClient.GetAsync("/api/v1/users")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TryLoginAsync(bystanderFarmCode, bystanderEmail)).StatusCode);
    }

    [Fact]
    public async Task ABearerWhoseEpochStillMatches_IsRejected_WhenTheFarmIsInactive()
    {
        var email = $"susp-gate-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var tokens = await factory.LoginAsync(email);
        var client = factory.CreateAuthedClient(tokens.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/users")).StatusCode);

        await DeactivateWithoutEpochBumpAsync(accountId);

        // The epoch still matches, so the epoch clause cannot reject this. Only
        // the AccountIsActive clause in the GATE can. Delete it and this is a 200.
        var response = await client.GetAsync("/api/v1/users");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal(AuthEndpoints.FarmSuspendedCode, problem!.Title);
    }

    // #532 round-5 — the fail-CLOSED distinction that `!= true` makes and
    // `== false` does not: a user row whose account row is MISSING yields a
    // NULL AccountIsActive in the correlated subquery. `!= true` rejects it
    // (null is not true); `== false` would let it through (null is not false)
    // and the request would proceed on a matching epoch. The two spellings are
    // indistinguishable for an existing inactive account — only a missing one
    // separates them. The FK is dropped for the window so the user can reference
    // a non-existent account, and restored before the test ends.
    [Fact]
    public async Task ABearerWhoseAccountRowIsMissing_IsRejected_WhenTheEpochStillMatches()
    {
        var email = $"susp-orphan-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var tokens = await factory.LoginAsync(email);
        var client = factory.CreateAuthedClient(tokens.AccessToken);

        // The epoch still matches (nothing has bumped it), so the ONLY clause
        // that can reject this is the account one — and it must do so even
        // though the account row is gone (AccountIsActive reads NULL, not
        // false). A `== false` implementation would let this 200.
        //
        // One explicit connection + transaction for the drop/delete so the DDL
        // and the row delete share a session: the FK must be gone before the
        // account row can be deleted, and both must commit before the request
        // below runs. A direct NpgsqlConnection (not EF's pooled DbConnection)
        // so the DDL and the parameterised delete are unambiguous.
        await using var conn = new Npgsql.NpgsqlConnection(factory.ConnectionString);
        await conn.OpenAsync();
        try
        {
            // The FK constraint name is mixed-case, so it MUST be quoted in the
            // DROP — an unquoted identifier is lowercased by Postgres and the
            // constraint silently survives (with IF EXISTS, a no-op).
            using (var drop = conn.CreateCommand())
            {
                drop.CommandText = "ALTER TABLE \"AspNetUsers\" DROP CONSTRAINT IF EXISTS \"FK_AspNetUsers_Accounts_AccountId\";";
                await drop.ExecuteNonQueryAsync();
            }
            using (var del = conn.CreateCommand())
            {
                del.CommandText = $"DELETE FROM \"Accounts\" WHERE \"Id\" = '{accountId}';";
                Assert.Equal(1, await del.ExecuteNonQueryAsync());
            }

            var response = await client.GetAsync("/api/v1/users");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
            Assert.Equal(AuthEndpoints.FarmSuspendedCode, problem!.Title);
        }
        finally
        {
            // Restore the FK so the shared schema is left intact for other tests
            // in this collection. The user row still references the deleted
            // account, so drop it first, then re-add the constraint. The try
            // starts BEFORE the DROP so every partially completed setup
            // (drop done, delete not yet; delete done, request not yet) reaches
            // this restore.
            using var tx = await conn.BeginTransactionAsync();
            var delUser = conn.CreateCommand();
            delUser.Transaction = tx;
            delUser.CommandText = "DELETE FROM \"AspNetUsers\" WHERE \"AccountId\" = @id;";
            delUser.Parameters.Add(new Npgsql.NpgsqlParameter("@id", accountId));
            await delUser.ExecuteNonQueryAsync();

            using var add = conn.CreateCommand();
            add.Transaction = tx;
            add.CommandText = "ALTER TABLE \"AspNetUsers\" ADD CONSTRAINT \"FK_AspNetUsers_Accounts_AccountId\" FOREIGN KEY (\"AccountId\") REFERENCES \"Accounts\" (\"Id\") ON DELETE RESTRICT;";
            await add.ExecuteNonQueryAsync();

            await tx.CommitAsync();
        }

        // Pin the restore: assert the constraint is present again. Replacing the
        // ADD CONSTRAINT above with a no-op leaves this assertion red — a
        // permanently broken FK is no longer invisible.
        using (var chk = conn.CreateCommand())
        {
            chk.CommandText = "SELECT COUNT(*) FROM pg_constraint WHERE conname = 'FK_AspNetUsers_Accounts_AccountId';";
            var count = (long)(await chk.ExecuteScalarAsync())!;
            Assert.True(count == 1, $"FK_AspNetUsers_Accounts_AccountId must be restored after the test (got {count})");
        }
    }

    [Fact]
    public async Task ARefreshTokenWhoseEpochStillMatches_IsRejected_WhenTheFarmIsInactive()
    {
        var email = $"susp-refresh-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var tokens = await factory.LoginAsync(email);

        await DeactivateWithoutEpochBumpAsync(accountId);

        // IssuedEpoch still equals the user's CredentialEpoch, so the epoch check
        // in RefreshAsync passes. Only the suspended-farm check rejects this.
        // Uri.UnescapeDataString because this bypasses HTTP: ExtractRefreshCookie
        // hands back the RAW Set-Cookie value, which is percent-encoded, and
        // RefreshAsync hashes whatever it is given. Without the decode the hash
        // matches no stored row and the method returns InvalidRefreshToken at its
        // FIRST branch — so this test would assert IsFailure and get it for a
        // reason that has nothing to do with the farm being suspended. Round 5
        // proved the suspended-farm guard was deletable with the whole suite
        // green because of exactly this. Same reason as RetryBoundaryTests.cs.
        using var scope = factory.Services.CreateScope();
        var identity = scope.ServiceProvider.GetRequiredService<IIdentityProvider>();
        Assert.True((await identity.RefreshAsync(tokens.RefreshTokenForDirectCall)).IsFailure,
            "a suspended farm must not rotate a session, even when the epoch still matches");
    }

    [Fact]
    public async Task ReactivationRevokesTheSessionsMintedBetweenSuspendAndReactivate()
    {
        var email = $"susp-reactivate-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        await SuspendAsync(accountId);

        // A refresh row that post-dates the suspension sweep — the exact artifact
        // the login/suspend race produces. Inserted directly because the race is
        // not reproducible on demand; what matters is that reactivation kills it.
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var userId = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
            var epoch = await db.Users.Where(u => u.Id == userId).Select(u => u.CredentialEpoch).SingleAsync();
            db.RefreshTokens.Add(new RefreshToken
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                AccountId = accountId,
                TokenHash = Guid.NewGuid().ToString("N"),
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
                IssuedEpoch = epoch,
            });
            await db.SaveChangesAsync();
        });
        Assert.Equal(1, await LiveRefreshTokenCountAsync(accountId));

        // Read BEFORE reactivation. Suspend already bumped these once; the
        // point of capturing the pre-reactivation value is to prove REACTIVATION
        // bumps them a SECOND time (round-5 review): without the reactivation
        // epoch/stamp update, a matching-epoch bearer minted in the instant
        // before the suspension committed would become usable again the moment
        // the farm comes back, for its remaining token lifetime. Refresh
        // revocation alone does not kill the already-issued access token.
        var userBefore = await ReadUserAsync(email);

        await ReactivateAsync(accountId);

        // Set revokeSessions to false on ReactivateAsync and this is the ONLY
        // test that reddens. The pre-suspension-session test does not: suspend
        // already bumped the epoch, so those credentials are dead either way.
        Assert.Equal(0, await LiveRefreshTokenCountAsync(accountId));

        // Reactivation revokes AND bumps, in the same statement as suspend does.
        var userAfter = await ReadUserAsync(email);
        Assert.Equal(userBefore.CredentialEpoch + 1, userAfter.CredentialEpoch);
        Assert.NotEqual(userBefore.SecurityStamp, userAfter.SecurityStamp);
        Assert.NotEqual(userBefore.ConcurrencyStamp, userAfter.ConcurrencyStamp);
    }

    [Fact]
    public async Task ReactivatingAnAlreadyActiveFarm_DoesNotSignAnybodyOut()
    {
        var email = $"susp-noop-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var tokens = await factory.LoginAsync(email);
        var client = factory.CreateAuthedClient(tokens.AccessToken);
        var before = await ReadUserAsync(email);

        // #534's reactivate verb is the kind of command an operator retries.
        await ReactivateAsync(accountId);

        var after = await ReadUserAsync(email);
        Assert.Equal(before.CredentialEpoch, after.CredentialEpoch);
        Assert.Equal(before.SecurityStamp, after.SecurityStamp);
        Assert.Equal(1, await LiveRefreshTokenCountAsync(accountId));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/users")).StatusCode);
    }

    [Fact]
    public async Task ADisabledUserInASuspendedFarm_IsToldTheirAccountIsDisabled()
    {
        var owner = $"susp-prec-owner-{Guid.NewGuid():N}@test.local";
        var victim = $"susp-prec-victim-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(owner);
        await factory.SeedUserAsync(accountId, victim, asAdmin: false);
        var tokens = await factory.LoginAsync(victim);
        var client = factory.CreateAuthedClient(tokens.AccessToken);

        // The middleware reads DisabledAt from the USER row, never from the JWT
        // claims, so this must be persisted. (The write ORDER here is not
        // load-bearing: the middleware reads the final row state at request time,
        // so disabling before or after the suspension's epoch bump gives the
        // identical outcome. What this test pins is the PRECEDENCE — that a
        // disabled user in a suspended farm is told their account is disabled,
        // not that the farm is suspended.)

        await factory.WithTenantScopeAsync(accountId, db =>
            db.Users.Where(u => u.Email == victim)
                .ExecuteUpdateAsync(setters => setters.SetProperty(u => u.DisabledAt, DateTimeOffset.UtcNow)));
        await SuspendAsync(accountId);

        // Both conditions are true at once. Precedence says DISABLED wins: the
        // farm's suspension is not this person's actionable problem. Swap the two
        // clauses in CredentialEpochMiddleware and only this test reddens.
        var response = await client.GetAsync("/api/v1/users");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("Auth.AccountDisabled", problem!.Title);
    }

    [Fact]
    public async Task SuspendAsync_ForAnUnknownAccount_ReturnsNotFound()
    {
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<AccountSuspensionService>();
        var result = await service.SuspendAsync(Guid.NewGuid(), reason: null);
        Assert.True(result.IsFailure);
    }

    // #532 round 8 — pins the percent-decode that round 5's fix silently
    // depended on. ExtractRefreshCookie returns the RAW Set-Cookie value,
    // which is percent-encoded. RefreshAsync hashes whatever it is given, so
    // the raw value hashes to nothing and the method fails at its FIRST
    // branch. This test asserts BOTH sides: raw fails, decoded succeeds.
    // If the encoding behaviour ever changes underneath these callers, this
    // test reddens.
    [Fact]
    public async Task RefreshToken_RawValue_Fails_DecodedValue_Succeeds_OnAnActiveFarm()
    {
        var email = $"susp-decode-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var tokens = await factory.LoginAsync(email);

        using var scope = factory.Services.CreateScope();
        var identity = scope.ServiceProvider.GetRequiredService<IIdentityProvider>();

        // Raw (percent-encoded) value: the hash matches no stored row →
        // InvalidRefreshToken at the first branch.
        var rawResult = await identity.RefreshAsync(tokens.RefreshToken);
        Assert.True(rawResult.IsFailure,
            "the raw percent-encoded refresh token must NOT be accepted");

        // Decoded value: the hash matches → a fresh pair is issued.
        var decodedResult = await identity.RefreshAsync(tokens.RefreshTokenForDirectCall);
        Assert.True(decodedResult.IsSuccess,
            "the decoded refresh token MUST be accepted on an active farm");
    }
}
