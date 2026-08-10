namespace Cluckwork.Api.IntegrationTests;

using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog.Core;
using Serilog.Events;

// #273 — the five stable structured security events the amendment to #273
// requires: failed login, account lockout, refresh-token replay detection,
// refresh revocation failure, and (in AuthRateLimitLoggingTests.cs) auth
// rate-limit rejection. Same CollectingSink tap RequestLoggingTests uses
// (#214), own factory/collection so this class's assertions never see another
// suite's traffic.
public sealed class SecurityEventLoggingFactory : CluckworkWebApplicationFactory
{
    public CollectingSink Sink { get; } = new();

    // #176 grace would otherwise let an immediate re-presentation of a just-
    // rotated token through as a benign retry instead of a genuine replay —
    // disabled so ReplayDetected/RevocationFailed tests are deterministic
    // without a real time delay.
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Jwt:RefreshReuseGraceSeconds", "0");
        builder.ConfigureTestServices(services =>
            services.AddSingleton<ILogEventSink>(Sink));
    }

    public sealed class CollectingSink : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
    }
}

[Collection(SecurityEventLoggingCollection.Name)]
public sealed class SecurityEventLoggingTests(SecurityEventLoggingFactory factory)
{
    private const int LockoutMaxFailedAttempts = 5; // CluckworkIdentityServiceCollectionExtensions: options.Lockout.MaxFailedAccessAttempts

    private static string? ScalarOf(LogEvent e, string name) =>
        e.Properties.TryGetValue(name, out var value) && value is ScalarValue scalar
            ? scalar.Value?.ToString()
            : null;

    private IReadOnlyList<LogEvent> EventsFor(string securityEvent) =>
        [.. factory.Sink.Events.Where(e => ScalarOf(e, "SecurityEvent") == securityEvent)];

    private async Task<string> SeedUserAsync()
    {
        var email = $"secevt-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);
        return email;
    }

    // ---------- Auth.LoginFailed — must not be an identity-existence oracle ----------

    [Fact]
    public async Task Failed_login_for_unknown_user_and_wrong_password_emit_the_identical_LoginFailed_shape()
    {
        factory.Sink.Events.Clear();
        var email = await SeedUserAsync();
        var client = factory.CreateClient();

        var unknownEmail = $"nobody-{Guid.NewGuid():N}@test.local";
        var unknown = await client.PostAsJsonAsync(
            "/api/v1/auth/login", new { email = unknownEmail, password = "WrongPassw0rd!x" });
        var wrongPassword = await client.PostAsJsonAsync(
            "/api/v1/auth/login", new { email, password = "WrongPassw0rd!x" });

        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);

        var events = EventsFor(SecurityEvents.LoginFailed);
        Assert.Equal(2, events.Count);
        foreach (var e in events)
        {
            // No user id, no email, on EITHER branch — the log must not carry a
            // signal the HTTP response (identical 401 on both) doesn't already
            // carry. The property set is exactly {SecurityEvent, ClientIp} plus
            // whatever Serilog/the request pipeline always attaches (TraceId etc).
            Assert.False(e.Properties.ContainsKey("UserId"));
            Assert.False(e.Properties.ContainsKey("Email"));
            Assert.NotNull(ScalarOf(e, "ClientIp"));
        }
        // Same property KEYS on both — no branch-specific field leaks through.
        var unknownKeys = events[0].Properties.Keys.OrderBy(k => k);
        var wrongKeys = events[1].Properties.Keys.OrderBy(k => k);
        Assert.Equal(unknownKeys, wrongKeys);
    }

    // ---------- Auth.AccountLockedOut — fires once, at the threshold crossing ----------

    [Fact]
    public async Task Login_attempts_crossing_the_lockout_threshold_emit_AccountLockedOut_exactly_once()
    {
        factory.Sink.Events.Clear();
        var email = await SeedUserAsync();
        var client = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var userId = (await users.FindByEmailAsync(email))!.Id;

        for (var i = 0; i < LockoutMaxFailedAttempts; i++)
            await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "WrongPassw0rd!x" });

        var lockedOutEvents = EventsFor(SecurityEvents.AccountLockedOut);
        var loginFailedEvents = EventsFor(SecurityEvents.LoginFailed);

        var locked = Assert.Single(lockedOutEvents);
        Assert.Equal(userId.ToString(), ScalarOf(locked, "UserId"));
        // Every attempt (including the one that also locked the account) is a
        // LoginFailed; only the last one ALSO gets AccountLockedOut.
        Assert.Equal(LockoutMaxFailedAttempts, loginFailedEvents.Count);
    }

    // ---------- Auth.LoginFailed / Auth.AccountLockedOut via /auth/step-up ----------
    // #273 codex review (P1b) — StepUpGrantService.IssueAsync is a SECOND
    // password oracle (#308's Owner-takeover re-confirmation) and previously
    // emitted neither event at all: it returned from every unsuccessful
    // branch directly and discarded RecordFailedAccessAsync's transition
    // bool. Mirrors the two LoginAsync tests above exactly, just against
    // /auth/step-up instead of /auth/login.

    [Fact]
    public async Task Failed_step_up_emits_LoginFailed_with_the_identical_shape_as_a_failed_login()
    {
        factory.Sink.Events.Clear();
        var email = await SeedUserAsync();
        var admin = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        var response = await admin.PostAsJsonAsync("/api/v1/auth/step-up", new { password = "WrongPassw0rd!x" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var events = EventsFor(SecurityEvents.LoginFailed);
        var e = Assert.Single(events);
        // Same non-enumerating shape as a failed /auth/login: no user id, no
        // email — see SecurityEvents.LoginFailed.
        Assert.False(e.Properties.ContainsKey("UserId"));
        Assert.False(e.Properties.ContainsKey("Email"));
        Assert.NotNull(ScalarOf(e, "ClientIp"));
    }

    [Fact]
    public async Task Step_up_attempts_crossing_the_lockout_threshold_emit_AccountLockedOut_exactly_once()
    {
        factory.Sink.Events.Clear();
        var email = await SeedUserAsync();

        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var userId = (await users.FindByEmailAsync(email))!.Id;

        var admin = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        for (var i = 0; i < LockoutMaxFailedAttempts; i++)
            await admin.PostAsJsonAsync("/api/v1/auth/step-up", new { password = "WrongPassw0rd!x" });

        var lockedOutEvents = EventsFor(SecurityEvents.AccountLockedOut);
        var loginFailedEvents = EventsFor(SecurityEvents.LoginFailed);

        var locked = Assert.Single(lockedOutEvents);
        Assert.Equal(userId.ToString(), ScalarOf(locked, "UserId"));
        // Every attempt (including the one that also locked the account) is a
        // LoginFailed; only the last one ALSO gets AccountLockedOut.
        Assert.Equal(LockoutMaxFailedAttempts, loginFailedEvents.Count);
    }

    // ---------- Auth.RefreshTokenReplayDetected ----------

    [Fact]
    public async Task Reuse_of_an_already_rotated_refresh_token_emits_ReplayDetected_exactly_once()
    {
        factory.Sink.Events.Clear();
        var email = await SeedUserAsync();

        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var userId = (await users.FindByEmailAsync(email))!.Id;

        var tokens = await factory.LoginAsync(email);
        var client = factory.CreateClient(new() { HandleCookies = false });

        // Rotate once — token A is now revoked, replaced by B. Grace is disabled
        // on this factory, so presenting A again below is unambiguously a replay.
        var rotated = await client.PostRefreshAsync(tokens.RefreshToken);
        rotated.EnsureSuccessStatusCode();

        var replay = await client.PostRefreshAsync(tokens.RefreshToken);

        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        var replayEvents = EventsFor(SecurityEvents.RefreshTokenReplayDetected);
        var replayEvent = Assert.Single(replayEvents);
        Assert.Equal(userId.ToString(), ScalarOf(replayEvent, "UserId"));
        // No revocation-failure event: the (real, unfaulted) revoke succeeded.
        Assert.Empty(EventsFor(SecurityEvents.RefreshRevocationFailed));
    }

    // ---------- Auth.RefreshRevocationFailed ----------

    // Fault-injects the bulk UPDATE that revokes a token family so the "safety
    // action itself failed" path is genuinely exercised, not just asserted by
    // reading the source. Same technique StepUpAuthTests.
    // RevokeRefreshTokenAsync_WhenTheBulkUpdateThrows_StillRecordsTheOwnersLogout
    // already proved works against this host — including the hard-won detail
    // that RefreshToken maps to the snake_cased "refresh_tokens" table, not the
    // CLR type name, which silently no-ops a naive match.
    private sealed class ThrowingRefreshTokenUpdateInterceptor : DbCommandInterceptor
    {
        public volatile bool Armed;

        // #273 codex review (round 2, P2c) — the revocation's PREREQUISITE read
        // (the owner lookup RevokeRefreshTokenAsync performs before the bulk
        // update) is a separate failure mode from the update itself, and needs
        // its own injection point: a SELECT against refresh_tokens rather than
        // an UPDATE.
        public volatile bool FailReadsInstead;

        private void MaybeFail(DbCommand command)
        {
            if (!Armed
                || !command.CommandText.Contains("refresh_tokens", StringComparison.OrdinalIgnoreCase))
                return;

            var statement = FailReadsInstead ? "SELECT" : "UPDATE";
            if (command.CommandText.Contains(statement, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Simulated refresh-token revoke failure (test fault injection).");
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            MaybeFail(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        // #273 codex review (P2e) test support — a TRACKED SaveChangesAsync
        // update against an entity with an IsConcurrencyToken property (like
        // RefreshToken.ConcurrencyStamp) is issued by Npgsql/EF as a command
        // with a RETURNING clause so the affected-row count can be read back,
        // which routes through ReaderExecutingAsync rather than
        // NonQueryExecutingAsync — confirmed by instrumenting this
        // interceptor and observing zero NonQueryExecutingAsync calls for the
        // ordinary (non-bulk) rotation path. The bulk ExecuteUpdateAsync calls
        // RunRevocationAsync wraps have no such concurrency check and go
        // through NonQueryExecutingAsync above instead, so both overrides are
        // needed to fault-inject every shape of "the UPDATE against
        // refresh_tokens throws."
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            MaybeFail(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task Refresh_replay_whose_revoke_throws_emits_RevocationFailed_and_still_fails_the_call()
    {
        factory.Sink.Events.Clear();
        var email = await SeedUserAsync();

        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByEmailAsync(email);
        Assert.NotNull(user);

        var tokens = await factory.LoginAsync(email);
        var client = factory.CreateClient(new() { HandleCookies = false });
        var rotated = await client.PostRefreshAsync(tokens.RefreshToken);
        rotated.EnsureSuccessStatusCode();

        // Direct construction (bypassing HTTP/DI for the DbContext only) so the
        // interceptor attaches to the EXACT context this call uses — mirrors
        // StepUpAuthTests' proven pattern; every other dependency comes off the
        // real host so login/token plumbing stays production code.
        var interceptor = new ThrowingRefreshTokenUpdateInterceptor();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(factory.ConnectionString)
            .AddInterceptors(interceptor)
            .Options;
        await using var db = new AppDbContext(options, new TenantContext());

        var provider = new IdentityProvider(
            userManager,
            services.GetRequiredService<RoleManager<ApplicationRole>>(),
            services.GetRequiredService<IJwtTokenService>(),
            db,
            services.GetRequiredService<IOptions<JwtOptions>>(),
            services.GetRequiredService<TimeProvider>(),
            services.GetRequiredService<IAuditWriter>(),
            services.GetRequiredService<IStepUpGrantRegistry>(),
            services.GetRequiredService<IHttpContextAccessor>(),
            services.GetRequiredService<AuthSecurityEventLogger>(),
            services.GetRequiredService<ILogger<IdentityProvider>>(),
            services.GetRequiredService<Cluckwork.Application.Features.Accounts.IAccountRepository>());

        var rawRefreshToken = Uri.UnescapeDataString(tokens.RefreshToken);

        interceptor.Armed = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.RefreshAsync(rawRefreshToken));
        interceptor.Armed = false;

        var replayEvents = EventsFor(SecurityEvents.RefreshTokenReplayDetected);
        var failedEvents = EventsFor(SecurityEvents.RefreshRevocationFailed);
        Assert.Single(replayEvents);
        var failed = Assert.Single(failedEvents);
        Assert.Equal(user!.Id.ToString(), ScalarOf(failed, "UserId"));
    }

    // #273 codex review (P2e) — RunRevocationAsync's wrapper only covered the
    // BULK-UPDATE revocations (family revoke, logout, break-glass/password
    // reset/change). Ordinary, NON-replay refresh rotation also revokes a
    // token (RefreshAsync sets stored.RevokedAt and SaveChangesAsync — a
    // tracked read-modify-save, not a bulk update) and previously sat outside
    // that hardening point entirely: a non-concurrency SaveChangesAsync
    // failure there silently skipped Auth.RefreshRevocationFailed even though
    // the class comment claimed every revocation was covered. Same fault-
    // injection technique as the replay test above, but exercised on the
    // FIRST (live, non-replay) rotation so this proves the OTHER code path.
    [Fact]
    public async Task Ordinary_refresh_rotation_whose_revoke_throws_emits_RevocationFailed_and_still_fails_the_call()
    {
        factory.Sink.Events.Clear();
        var email = await SeedUserAsync();

        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByEmailAsync(email);
        Assert.NotNull(user);

        var tokens = await factory.LoginAsync(email);

        var interceptor = new ThrowingRefreshTokenUpdateInterceptor();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(factory.ConnectionString)
            .AddInterceptors(interceptor)
            .Options;
        await using var db = new AppDbContext(options, new TenantContext());

        var provider = new IdentityProvider(
            userManager,
            services.GetRequiredService<RoleManager<ApplicationRole>>(),
            services.GetRequiredService<IJwtTokenService>(),
            db,
            services.GetRequiredService<IOptions<JwtOptions>>(),
            services.GetRequiredService<TimeProvider>(),
            services.GetRequiredService<IAuditWriter>(),
            services.GetRequiredService<IStepUpGrantRegistry>(),
            services.GetRequiredService<IHttpContextAccessor>(),
            services.GetRequiredService<AuthSecurityEventLogger>(),
            services.GetRequiredService<ILogger<IdentityProvider>>(),
            services.GetRequiredService<Cluckwork.Application.Features.Accounts.IAccountRepository>());

        var rawRefreshToken = Uri.UnescapeDataString(tokens.RefreshToken);

        // This is the token's FIRST rotation — not a replay — so it exercises
        // RefreshAsync's tracked-save revocation, never the bulk-update path
        // RunRevocationAsync wraps. Note the exception TYPE differs from the
        // bulk-update replay test above: a tracked SaveChangesAsync failure
        // arrives wrapped as DbUpdateException (EF Core's own wrapping around
        // the ReaderExecutingAsync fault this interceptor injects), where
        // ExecuteUpdateAsync's bulk path lets the raw InvalidOperationException
        // through unwrapped. Either way it is NOT a DbUpdateConcurrencyException,
        // so it takes the log-and-rethrow branch, not the benign-race branch.
        interceptor.Armed = true;
        await Assert.ThrowsAsync<DbUpdateException>(() => provider.RefreshAsync(rawRefreshToken));
        interceptor.Armed = false;

        // Not a replay: ReplayDetected must stay silent so this failure is
        // attributable to the right event.
        Assert.Empty(EventsFor(SecurityEvents.RefreshTokenReplayDetected));
        var failed = Assert.Single(EventsFor(SecurityEvents.RefreshRevocationFailed));
        Assert.Equal(user!.Id.ToString(), ScalarOf(failed, "UserId"));
    }

    // #273 codex review (round 2, P2c) — logout's owner LOOKUP sat outside the
    // failure boundary: RevokeRefreshTokenAsync read the token's owner before
    // entering the wrapped revoke, so a read failure meant the revoke never ran
    // AND Auth.RefreshRevocationFailed was never emitted — the revocation
    // silently failed with nothing for a deployment to alert on. Fault-injects
    // the SELECT rather than the UPDATE, which is the whole point: the UPDATE
    // path was already covered.
    [Fact]
    public async Task Logout_whose_owner_lookup_throws_emits_RevocationFailed()
    {
        factory.Sink.Events.Clear();
        var email = await SeedUserAsync();

        using var scope = factory.Services.CreateScope();
        var tokens = await factory.LoginAsync(email);

        var interceptor = new ThrowingRefreshTokenUpdateInterceptor { FailReadsInstead = true };
        await using var db = InterceptedDbContext(interceptor);
        var provider = IdentityProviderOn(db, scope.ServiceProvider);

        interceptor.Armed = true;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.RevokeRefreshTokenAsync(Uri.UnescapeDataString(tokens.RefreshToken)));
        interceptor.Armed = false;

        Assert.Single(EventsFor(SecurityEvents.RefreshRevocationFailed));
    }

    // The other half of the same fix: extending the boundary backwards over the
    // lookup must not make a failing UPDATE emit the event TWICE (once from the
    // widened boundary, once from the per-statement wrapper the code used to
    // have). Exactly one line per failed logout, whichever statement failed.
    [Fact]
    public async Task Logout_whose_bulk_update_throws_emits_RevocationFailed_exactly_once()
    {
        factory.Sink.Events.Clear();
        var email = await SeedUserAsync();

        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var userId = (await users.FindByEmailAsync(email))!.Id;
        var tokens = await factory.LoginAsync(email);

        var interceptor = new ThrowingRefreshTokenUpdateInterceptor();
        await using var db = InterceptedDbContext(interceptor);
        var provider = IdentityProviderOn(db, scope.ServiceProvider);

        interceptor.Armed = true;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.RevokeRefreshTokenAsync(Uri.UnescapeDataString(tokens.RefreshToken)));
        interceptor.Armed = false;

        var failed = Assert.Single(EventsFor(SecurityEvents.RefreshRevocationFailed));
        // The lookup succeeded here, so the owner IS known — the widened
        // boundary must not have cost the event its correlation field.
        Assert.Equal(userId.ToString(), ScalarOf(failed, "UserId"));
    }

    // Direct construction (bypassing HTTP/DI for the DbContext only) so the
    // interceptor attaches to the EXACT context the call under test uses —
    // mirrors StepUpAuthTests' proven pattern; every other dependency comes off
    // the real host so login/token plumbing stays production code.
    private AppDbContext InterceptedDbContext(DbCommandInterceptor interceptor) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(factory.ConnectionString)
                .AddInterceptors(interceptor)
                .Options,
            new TenantContext());

    private static IdentityProvider IdentityProviderOn(AppDbContext db, IServiceProvider services) =>
        new(services.GetRequiredService<UserManager<ApplicationUser>>(),
            services.GetRequiredService<RoleManager<ApplicationRole>>(),
            services.GetRequiredService<IJwtTokenService>(),
            db,
            services.GetRequiredService<IOptions<JwtOptions>>(),
            services.GetRequiredService<TimeProvider>(),
            services.GetRequiredService<IAuditWriter>(),
            services.GetRequiredService<IStepUpGrantRegistry>(),
            services.GetRequiredService<IHttpContextAccessor>(),
            services.GetRequiredService<AuthSecurityEventLogger>(),
            services.GetRequiredService<ILogger<IdentityProvider>>(),
            services.GetRequiredService<Cluckwork.Application.Features.Accounts.IAccountRepository>());
}

[CollectionDefinition(Name)]
public sealed class SecurityEventLoggingCollection : ICollectionFixture<SecurityEventLoggingFactory>
{
    public const string Name = "security-event-logging";
}
