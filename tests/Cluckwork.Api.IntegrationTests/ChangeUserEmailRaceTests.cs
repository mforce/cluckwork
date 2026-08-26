namespace Cluckwork.Api.IntegrationTests;

using System.Data.Common;
using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Users.ChangeUserEmail;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Auditing;
using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

public sealed class ChangeUserEmailFinalSaveFactory : CluckworkWebApplicationFactory
{
    public ChangeUserEmailFinalSaveInterceptor Interceptor { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
            services.AddDbContext<AppDbContext>((_, options) => options.AddInterceptors(Interceptor)));
    }
}

public sealed class ChangeUserEmailFinalSaveInterceptor : DbCommandInterceptor
{
    private int _armed;

    public void Arm() => Interlocked.Exchange(ref _armed, 1);

    private void MaybeFail(DbCommand command)
    {
        if (command.CommandText.Contains("INSERT INTO \"AuditEvents\"", StringComparison.Ordinal)
            && Interlocked.CompareExchange(ref _armed, 0, 1) == 1)
            throw new DbUpdateConcurrencyException("Simulated final change-email concurrency failure.");
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        MaybeFail(command);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        MaybeFail(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
}

[Collection(IntegrationCollection.Name)]
public sealed class ChangeUserEmailRaceTests(CluckworkWebApplicationFactory factory)
{
    private sealed record StepUpDto(string Token, DateTimeOffset ExpiresAt);

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}@test.local";

    private async Task<(Guid AccountId, string Owner, Guid OwnerId)> SeedFarmAsync()
    {
        var owner = Unique("owner");
        var accountId = await factory.SeedAccountWithUserAsync(owner);
        return (accountId, owner, await UserIdAsync(accountId, owner));
    }

    private Task<Guid> UserIdAsync(Guid accountId, string email) =>
        factory.WithTenantScopeAsync(accountId, db => db.Users
            .Where(u => u.AccountId == accountId && u.Email == email)
            .Select(u => u.Id)
            .SingleAsync());

    private async Task<string> StepUpAsync(string email)
    {
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/step-up", new { password = TestHarness.Password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<StepUpDto>())!.Token;
    }

    private async Task<Result> InvokeAsync(
        Guid accountId, Guid userId, string email, Guid actingUserId, string? proof = null)
    {
        using var scope = factory.Services.CreateScope();
        scope.ResolveTenantAndActor(accountId, actingUserId);
        return await scope.ServiceProvider.GetRequiredService<ChangeUserEmailHandler>().HandleAsync(
            new ChangeUserEmailCommand(userId, email, proof),
            accountId, actingUserId, CancellationToken.None);
    }

    private async Task<(AppDbContext Db, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction Tx, int Pid)>
        FenceAccountAsync(Guid accountId)
    {
        var tenant = new TenantContext();
        tenant.Resolve(accountId);
        var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(factory.ConnectionString).Options, tenant);
        var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""SELECT 1 FROM "Accounts" WHERE "Id" = {accountId} FOR UPDATE""");
        return (db, tx, await db.BackendPidAsync());
    }

    [Fact]
    public async Task RefreshInFlightAcrossEmailChange_LeavesNoUsableChild()
    {
        var (accountId, owner, ownerId) = await SeedFarmAsync();
        var target = Unique("target");
        await factory.SeedUserAsync(accountId, target, "Manager");
        var targetId = await UserIdAsync(accountId, target);
        var session = await factory.LoginAsync(target);
        var proof = await StepUpAsync(owner);
        var (db, tx, pid) = await FenceAccountAsync(accountId);
        await using var _ = db;
        await using var __ = tx;

        var change = Task.Run(() => InvokeAsync(accountId, targetId, Unique("changed"), ownerId, proof));
        Assert.True(await factory.WaitUntilDoneOrBlockedAsync(change, pid));

        var refresh = await factory.CreateClient().PostRefreshAsync(
            session.RefreshToken, expectedAccount: accountId.ToString());
        refresh.EnsureSuccessStatusCode();
        var child = await TestHarness.ReadTokensAsync(refresh);

        await tx.RollbackAsync();
        Assert.True((await change).IsSuccess);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await factory.CreateAuthedClient(child.AccessToken).GetAsync("/api/v1/flocks")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await factory.CreateClient().PostRefreshAsync(
                child.RefreshToken, expectedAccount: accountId.ToString())).StatusCode);
    }

    [Fact]
    public async Task ConcurrentSameFarmClaims_OneWins_AndLoserIsDuplicateEmail()
    {
        var (accountId, owner, ownerId) = await SeedFarmAsync();
        var first = Unique("first");
        var second = Unique("second");
        await factory.SeedUserAsync(accountId, first, "Manager");
        await factory.SeedUserAsync(accountId, second, "Manager");
        var firstId = await UserIdAsync(accountId, first);
        var secondId = await UserIdAsync(accountId, second);
        var claimed = Unique("claimed");
        var firstProof = await StepUpAsync(owner);
        var secondProof = await StepUpAsync(owner);
        var (db, tx, pid) = await FenceAccountAsync(accountId);
        await using var _ = db;
        await using var __ = tx;

        var firstClaim = Task.Run(() => InvokeAsync(accountId, firstId, claimed, ownerId, firstProof));
        Assert.True(await factory.WaitUntilDoneOrBlockedAsync(firstClaim, pid));
        var secondClaim = Task.Run(() => InvokeAsync(accountId, secondId, claimed, ownerId, secondProof));
        Assert.True(await factory.WaitUntilDoneOrBlockedAsync(secondClaim, pid, minBlockedCount: 2));

        await tx.RollbackAsync();
        var results = await Task.WhenAll(firstClaim, secondClaim);

        Assert.Single(results, result => result.IsSuccess);
        var loser = Assert.Single(results, result => result.IsFailure);
        Assert.Equal("Users.DuplicateEmail", loser.Error.Code);
        Assert.Equal(1, await factory.WithTenantScopeAsync(accountId,
            d => d.Users.CountAsync(u => u.AccountId == accountId && u.NormalizedEmail == claimed.ToUpperInvariant())));
    }

    [Fact]
    public async Task ConcurrentStampChange_DuringEmailMutation_Is409()
    {
        var (accountId, owner, ownerId) = await SeedFarmAsync();
        var target = Unique("target");
        await factory.SeedUserAsync(accountId, target, "Manager");
        var targetId = await UserIdAsync(accountId, target);
        var proof = await StepUpAsync(owner);
        var tenant = new TenantContext();
        tenant.Resolve(accountId);
        await using var fenceDb = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(factory.ConnectionString).Options, tenant);
        await using var fenceTx = await fenceDb.Database.BeginTransactionAsync();
        await fenceDb.Database.ExecuteSqlInterpolatedAsync(
            $"""SELECT 1 FROM "AspNetUsers" WHERE "Id" = {targetId} FOR UPDATE""");
        var fencePid = await fenceDb.BackendPidAsync();

        var change = Task.Run(() => InvokeAsync(accountId, targetId, Unique("changed"), ownerId, proof));
        Assert.True(await factory.WaitUntilDoneOrBlockedAsync(change, fencePid));
        await fenceDb.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE "AspNetUsers" SET "ConcurrencyStamp" = {Guid.NewGuid().ToString()} WHERE "Id" = {targetId}""");
        await fenceTx.CommitAsync();

        var result = await change;
        Assert.True(result.IsFailure);
        Assert.Equal("Users.Conflict", result.Error.Code);
        Assert.Equal(target, await factory.WithTenantScopeAsync(accountId,
            d => d.Users.Where(u => u.Id == targetId).Select(u => u.Email).SingleAsync()));
    }

    [Fact]
    public async Task DuplicateFailure_RestoresTrackedFieldsAndStampsBeforeLaterSave()
    {
        var (accountId, _, ownerId) = await SeedFarmAsync();
        var existing = Unique("existing");
        var target = Unique("target");
        var bystander = Unique("bystander");
        await factory.SeedUserAsync(accountId, existing, "Manager");
        await factory.SeedUserAsync(accountId, target, "Manager");
        await factory.SeedUserAsync(accountId, bystander, "Manager");
        var targetId = await UserIdAsync(accountId, target);
        var bystanderId = await UserIdAsync(accountId, bystander);

        using var scope = factory.Services.CreateScope();
        scope.ResolveTenantAndActor(accountId, ownerId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tracked = await db.Users.SingleAsync(u => u.Id == targetId);
        var oldStamp = tracked.SecurityStamp;
        var oldConcurrency = tracked.ConcurrencyStamp;
        var result = await scope.ServiceProvider.GetRequiredService<IIdentityProvider>()
            .ChangeUserEmailAsync(accountId, targetId, existing, ownerId);

        Assert.True(result.IsFailure);
        Assert.Equal("Users.DuplicateEmail", result.Error.Code);
        var unrelated = await db.Users.SingleAsync(u => u.Id == bystanderId);
        unrelated.DisplayName = "Saved after duplicate";
        await db.SaveChangesAsync();

        using var fresh = factory.Services.CreateScope();
        fresh.ServiceProvider.GetRequiredService<TenantContext>().Resolve(accountId);
        var check = await fresh.ServiceProvider.GetRequiredService<AppDbContext>().Users
            .AsNoTracking().SingleAsync(u => u.Id == targetId);
        Assert.Equal(target, check.Email);
        Assert.Equal(target, check.UserName);
        Assert.Equal(oldStamp, check.SecurityStamp);
        Assert.Equal(oldConcurrency, check.ConcurrencyStamp);
    }

    [Fact]
    public async Task FinalConcurrencyFailure_RemovesEpochAndPendingAuditFromTracker()
    {
        await using var injected = new ChangeUserEmailFinalSaveFactory();
        await injected.InitializeAsync();
        var owner = Unique("owner");
        var accountId = await injected.SeedAccountWithUserAsync(owner);
        var ownerId = await injected.WithTenantScopeAsync(accountId,
            db => db.Users.Where(u => u.Email == owner).Select(u => u.Id).SingleAsync());
        var target = Unique("target");
        await injected.SeedUserAsync(accountId, target, "Manager");
        var targetId = await injected.WithTenantScopeAsync(accountId,
            db => db.Users.Where(u => u.Email == target).Select(u => u.Id).SingleAsync());

        using var scope = injected.Services.CreateScope();
        scope.ResolveTenantAndActor(accountId, ownerId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tracked = await db.Users.SingleAsync(u => u.Id == targetId);
        var oldEpoch = tracked.CredentialEpoch;
        injected.Interceptor.Arm();

        var result = await scope.ServiceProvider.GetRequiredService<IIdentityProvider>()
            .ChangeUserEmailAsync(accountId, targetId, Unique("changed"), ownerId);

        Assert.True(result.IsFailure);
        Assert.Equal("Users.Conflict", result.Error.Code);
        Assert.Equal(oldEpoch, tracked.CredentialEpoch);
        Assert.DoesNotContain(db.ChangeTracker.Entries<AuditEvent>(),
            entry => entry.State == EntityState.Added && entry.Entity.Action == "User.EmailChanged");
    }

    [Fact]
    public async Task DisabledCoOwner_DoesNotSatisfySoleOwnerSelfChangeGuard()
    {
        var (accountId, owner, ownerId) = await SeedFarmAsync();
        var disabled = Unique("disabled-owner");
        await factory.SeedUserAsync(accountId, disabled, Roles.Owner);
        var disabledId = await UserIdAsync(accountId, disabled);
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            (await db.Users.SingleAsync(u => u.Id == disabledId)).DisabledAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        });

        var result = await InvokeAsync(accountId, ownerId, Unique("changed"), ownerId, await StepUpAsync(owner));

        Assert.True(result.IsFailure);
        Assert.Equal("Users.LastOwner", result.Error.Code);
    }

    [Fact]
    public async Task ActorDemotedWhileQueued_Is403_AndTargetIsUnchanged()
    {
        var (accountId, actor, actorId) = await SeedFarmAsync();
        var coOwner = Unique("co-owner");
        await factory.SeedUserAsync(accountId, coOwner, Roles.Owner);
        var target = Unique("target");
        await factory.SeedUserAsync(accountId, target, "Manager");
        var targetId = await UserIdAsync(accountId, target);
        var proof = await StepUpAsync(actor);
        var (db, tx, pid) = await FenceAccountAsync(accountId);
        await using var _ = db;
        await using var __ = tx;

        var change = Task.Run(() => InvokeAsync(accountId, targetId, Unique("changed"), actorId, proof));
        Assert.True(await factory.WaitUntilDoneOrBlockedAsync(change, pid));
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM "AspNetUserRoles"
            WHERE "UserId" = {actorId}
              AND "RoleId" IN (SELECT "Id" FROM "AspNetRoles" WHERE "Name" = {Roles.Owner})
            """);
        await tx.CommitAsync();

        var result = await change;
        Assert.True(result.IsFailure);
        Assert.Equal("Auth.Forbidden", result.Error.Code);
        Assert.Equal(target, await factory.WithTenantScopeAsync(accountId,
            d => d.Users.Where(u => u.Id == targetId).Select(u => u.Email).SingleAsync()));
    }

    [Fact]
    public void IsUserEmailConflict_AcceptsOnlyTheTwoNamedUniqueConstraints()
    {
        static DbUpdateException Failure(string sqlState, string constraint) => new(
            "failed",
            new PostgresException(
                "duplicate", "ERROR", "ERROR", sqlState,
                "detail", "hint", 0, 0, "query", "where", "public", "AspNetUsers",
                "NormalizedEmail", "text", constraint, "file", "1", "routine"));

        Assert.True(IdentityProvider.IsUserEmailConflict(
            Failure(PostgresErrorCodes.UniqueViolation, "EmailIndex")));
        Assert.True(IdentityProvider.IsUserEmailConflict(
            Failure(PostgresErrorCodes.UniqueViolation, "UserNameIndex")));
        Assert.False(IdentityProvider.IsUserEmailConflict(
            Failure(PostgresErrorCodes.UniqueViolation, "RoleNameIndex")));
        Assert.False(IdentityProvider.IsUserEmailConflict(
            Failure(PostgresErrorCodes.ForeignKeyViolation, "EmailIndex")));
    }
}
