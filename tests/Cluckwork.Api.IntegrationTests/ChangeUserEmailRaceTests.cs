namespace Cluckwork.Api.IntegrationTests;

using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Cluckwork.Api.Endpoints.Auth;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Users.ChangeUserEmail;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Auditing;
using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
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
    private sealed record TrackedEntryState(
        EntityState State,
        IReadOnlyDictionary<string, object?> CurrentValues,
        IReadOnlyDictionary<string, object?> OriginalValues,
        IReadOnlyDictionary<string, bool> ModifiedFlags);

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}@test.local";

    private static TrackedEntryState Capture(EntityEntry entry) => new(
        entry.State,
        entry.Properties.ToDictionary(property => property.Metadata.Name, property => property.CurrentValue),
        entry.Properties.ToDictionary(property => property.Metadata.Name, property => property.OriginalValue),
        entry.Properties.ToDictionary(property => property.Metadata.Name, property => property.IsModified));

    private static void AssertTrackedEntryState(TrackedEntryState expected, EntityEntry actual)
    {
        Assert.Equal(expected.State, actual.State);
        foreach (var property in actual.Properties)
        {
            var name = property.Metadata.Name;
            Assert.Equal(expected.CurrentValues[name], property.CurrentValue);
            Assert.Equal(expected.OriginalValues[name], property.OriginalValue);
            Assert.Equal(expected.ModifiedFlags[name], property.IsModified);
        }
    }

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

    private static Task<HttpResponseMessage> InvokeHttpAsync(
        HttpClient client, Guid userId, string email, string proof)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/users/{userId}/email")
        {
            Content = JsonContent.Create(new { email })
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Headers.Add(AuthEndpoints.StepUpHeaderName, proof);
        return client.SendAsync(request);
    }

    private async Task<(AppDbContext Db, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction Tx, int Pid)>
        FenceAccountAsync(Guid accountId)
    {
        var tenant = new TenantContext();
        tenant.Resolve(accountId);
        var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(factory.ConnectionString).Options, tenant, new FlockScope());
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
    public async Task ConcurrentEmailChanges_AccountLockSerializes_AndFriendlyValidatorRejectsLoser()
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

        // Both email-change operations take the same account lock before their
        // Identity validator runs. The second request therefore cannot race
        // the first to PostgreSQL's unique constraint: it resumes after the
        // winner commits and the account-scoped validator reports the friendly
        // duplicate result from the now-persisted row.
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
    public async Task QueuedSelfChange_UsesPostLockEmail_AndCannotReturnFalseNoOp()
    {
        var (accountId, owner, ownerId) = await SeedFarmAsync();
        await factory.SeedUserAsync(accountId, Unique("co-owner"), Roles.Owner);
        var firstEmail = Unique("first");
        var firstProof = await StepUpAsync(owner);
        var secondProof = await StepUpAsync(owner);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(owner));
        var (db, tx, pid) = await FenceAccountAsync(accountId);
        await using var _ = db;
        await using var __ = tx;

        // Both HTTP requests validate and consume their distinct one-use grants
        // before queueing on the account row. The second asks for the email that
        // was current before the first request. Once the first commits, the
        // second must observe that post-lock row and apply its requested last
        // value (or report an explicit conflict), never identity-resolve its
        // stale step-up instance into a false no-op 204.
        var first = Task.Run(() => InvokeHttpAsync(client, ownerId, firstEmail, firstProof));
        Assert.True(await factory.WaitUntilDoneOrBlockedAsync(first, pid));
        var second = Task.Run(() => InvokeHttpAsync(client, ownerId, owner, secondProof));
        Assert.True(await factory.WaitUntilDoneOrBlockedAsync(second, pid, minBlockedCount: 2));

        await tx.RollbackAsync();
        var firstResponse = await first;
        var secondResponse = await second;

        Assert.Equal(HttpStatusCode.NoContent, firstResponse.StatusCode);
        if (secondResponse.StatusCode == HttpStatusCode.NoContent)
        {
            Assert.Equal(owner, await factory.WithTenantScopeAsync(accountId,
                scoped => scoped.Users.Where(user => user.Id == ownerId)
                    .Select(user => user.Email).SingleAsync()));
        }
        else
        {
            Assert.Equal(HttpStatusCode.Conflict, secondResponse.StatusCode);
            Assert.Equal("Users.Conflict",
                (await secondResponse.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);
        }
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
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(factory.ConnectionString).Options, tenant, new FlockScope());
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
    public async Task SuccessfulChange_PreservesCallerOwnedDirtyEntriesUntilTheirLaterSave()
    {
        var (accountId, _, ownerId) = await SeedFarmAsync();
        var targetEmail = Unique("target");
        var unrelatedEmail = Unique("unrelated");
        await factory.SeedUserAsync(accountId, targetEmail, "Manager");
        await factory.SeedUserAsync(accountId, unrelatedEmail, "Manager");
        var targetId = await UserIdAsync(accountId, targetEmail);
        var unrelatedId = await UserIdAsync(accountId, unrelatedEmail);
        var deletedClaimType = $"pending-delete-{Guid.NewGuid():N}";
        var deletedClaimId = await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var claim = new IdentityUserClaim<Guid>
            {
                UserId = unrelatedId,
                ClaimType = deletedClaimType,
                ClaimValue = "still-present-until-caller-save"
            };
            db.UserClaims.Add(claim);
            await db.SaveChangesAsync();
            return claim.Id;
        });

        using var scope = factory.Services.CreateScope();
        scope.ResolveTenantAndActor(accountId, ownerId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var target = await db.Users.SingleAsync(user => user.Id == targetId);
        var oldTargetDisplayName = target.DisplayName;
        target.DisplayName = "Pending target display name";
        var targetEntry = db.Entry(target);
        var unrelated = await db.Users.SingleAsync(user => user.Id == unrelatedId);
        unrelated.DisplayName = "Pending unrelated display name";
        var unrelatedEntry = db.Entry(unrelated);
        var expectedUnrelatedState = Capture(unrelatedEntry);
        var addedClaimType = $"pending-add-{Guid.NewGuid():N}";
        var addedClaim = new IdentityUserClaim<Guid>
        {
            UserId = unrelatedId,
            ClaimType = addedClaimType,
            ClaimValue = "inserted-by-caller-save"
        };
        db.UserClaims.Add(addedClaim);
        var addedClaimEntry = db.Entry(addedClaim);
        var temporaryAddedClaimId = addedClaim.Id;
        Assert.True(addedClaimEntry.Property(claim => claim.Id).IsTemporary);
        var expectedAddedClaimState = Capture(addedClaimEntry);
        var deletedClaim = await db.UserClaims.SingleAsync(claim => claim.Id == deletedClaimId);
        db.UserClaims.Remove(deletedClaim);
        var deletedClaimEntry = db.Entry(deletedClaim);
        var expectedDeletedClaimState = Capture(deletedClaimEntry);
        var changedEmail = Unique("changed");
        var oldUnrelatedDisplayName = unrelatedEntry.Property(user => user.DisplayName).OriginalValue;

        var result = await scope.ServiceProvider.GetRequiredService<IIdentityProvider>()
            .ChangeUserEmailAsync(accountId, targetId, changedEmail, ownerId);

        Assert.True(result.IsSuccess);
        Assert.Equal(EntityState.Modified, targetEntry.State);
        Assert.Equal("Pending target display name", target.DisplayName);
        Assert.Equal(oldTargetDisplayName,
            targetEntry.Property(user => user.DisplayName).OriginalValue);
        Assert.True(targetEntry.Property(user => user.DisplayName).IsModified);
        Assert.Equal(changedEmail, target.Email);
        Assert.Equal(changedEmail, targetEntry.Property(user => user.Email).OriginalValue);
        Assert.False(targetEntry.Property(user => user.Email).IsModified);
        AssertTrackedEntryState(expectedUnrelatedState, unrelatedEntry);
        AssertTrackedEntryState(expectedAddedClaimState, addedClaimEntry);
        Assert.True(addedClaimEntry.Property(claim => claim.Id).IsTemporary);
        Assert.Equal(temporaryAddedClaimId, addedClaim.Id);
        AssertTrackedEntryState(expectedDeletedClaimState, deletedClaimEntry);

        using (var beforeCallerSave = factory.Services.CreateScope())
        {
            beforeCallerSave.ServiceProvider.GetRequiredService<TenantContext>().Resolve(accountId);
            var freshDb = beforeCallerSave.ServiceProvider.GetRequiredService<AppDbContext>();
            var persistedTarget = await freshDb.Users.AsNoTracking().SingleAsync(user => user.Id == targetId);
            Assert.Equal(changedEmail, persistedTarget.Email);
            Assert.Equal(oldTargetDisplayName, persistedTarget.DisplayName);
            Assert.Equal(oldUnrelatedDisplayName,
                await freshDb.Users.Where(user => user.Id == unrelatedId)
                    .Select(user => user.DisplayName).SingleAsync());
            Assert.False(await freshDb.UserClaims.AnyAsync(claim => claim.ClaimType == addedClaimType));
            Assert.True(await freshDb.UserClaims.AnyAsync(claim => claim.Id == deletedClaimId));
        }

        await db.SaveChangesAsync();

        using var afterCallerSave = factory.Services.CreateScope();
        afterCallerSave.ServiceProvider.GetRequiredService<TenantContext>().Resolve(accountId);
        var afterDb = afterCallerSave.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal("Pending target display name",
            await afterDb.Users.Where(user => user.Id == targetId).Select(user => user.DisplayName).SingleAsync());
        Assert.Equal("Pending unrelated display name",
            await afterDb.Users.Where(user => user.Id == unrelatedId).Select(user => user.DisplayName).SingleAsync());
        Assert.True(await afterDb.UserClaims.AnyAsync(claim => claim.ClaimType == addedClaimType));
        Assert.False(await afterDb.UserClaims.AnyAsync(claim => claim.Id == deletedClaimId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalConcurrencyFailure_RemovesEpochAndPendingAuditFromTracker(bool targetWasModified)
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
        var preexistingEmail = Unique("preexisting");
        await injected.SeedUserAsync(accountId, preexistingEmail, "Manager");
        var preexistingId = await injected.WithTenantScopeAsync(accountId,
            db => db.Users.Where(u => u.Email == preexistingEmail).Select(u => u.Id).SingleAsync());
        var laterEmail = Unique("later");
        await injected.SeedUserAsync(accountId, laterEmail, "Manager");
        var laterId = await injected.WithTenantScopeAsync(accountId,
            db => db.Users.Where(u => u.Email == laterEmail).Select(u => u.Id).SingleAsync());
        var deletedClaimType = $"pending-delete-{Guid.NewGuid():N}";
        var deletedClaimId = await injected.WithTenantScopeAsync(accountId, async db =>
        {
            var claim = new IdentityUserClaim<Guid>
            {
                UserId = preexistingId,
                ClaimType = deletedClaimType,
                ClaimValue = "still-present-until-caller-save"
            };
            db.UserClaims.Add(claim);
            await db.SaveChangesAsync();
            return claim.Id;
        });

        using var scope = injected.Services.CreateScope();
        scope.ResolveTenantAndActor(accountId, ownerId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tracked = await db.Users.SingleAsync(u => u.Id == targetId);
        var pendingDisplayName = targetWasModified ? "Pending before email change" : tracked.DisplayName;
        if (targetWasModified)
            tracked.DisplayName = pendingDisplayName;
        var targetEntry = db.Entry(tracked);
        var expectedTargetState = Capture(targetEntry);
        var oldEmail = tracked.Email;
        var oldUserName = tracked.UserName;
        var oldSecurityStamp = tracked.SecurityStamp;
        var oldConcurrencyStamp = tracked.ConcurrencyStamp;
        var oldEpoch = tracked.CredentialEpoch;
        var preexisting = await db.Users.SingleAsync(u => u.Id == preexistingId);
        preexisting.DisplayName = "Pending before email change";
        var preexistingEntry = db.Entry(preexisting);
        var expectedPreexistingState = Capture(preexistingEntry);
        var addedClaimType = $"pending-add-{Guid.NewGuid():N}";
        var addedClaim = new IdentityUserClaim<Guid>
        {
            UserId = preexistingId,
            ClaimType = addedClaimType,
            ClaimValue = "inserted-by-caller-save"
        };
        db.UserClaims.Add(addedClaim);
        var addedClaimEntry = db.Entry(addedClaim);
        var temporaryAddedClaimId = addedClaim.Id;
        Assert.True(addedClaimEntry.Property(claim => claim.Id).IsTemporary);
        var expectedAddedClaimState = Capture(addedClaimEntry);
        var deletedClaim = await db.UserClaims.SingleAsync(claim => claim.Id == deletedClaimId);
        db.UserClaims.Remove(deletedClaim);
        var deletedClaimEntry = db.Entry(deletedClaim);
        var expectedDeletedClaimState = Capture(deletedClaimEntry);
        injected.Interceptor.Arm();

        var result = await scope.ServiceProvider.GetRequiredService<IIdentityProvider>()
            .ChangeUserEmailAsync(accountId, targetId, Unique("changed"), ownerId);

        Assert.True(result.IsFailure);
        Assert.Equal("Users.Conflict", result.Error.Code);
        Assert.Equal(oldEpoch, tracked.CredentialEpoch);
        AssertTrackedEntryState(expectedTargetState, targetEntry);
        Assert.DoesNotContain(db.ChangeTracker.Entries<AuditEvent>(),
            entry => entry.State == EntityState.Added && entry.Entity.Action == "User.EmailChanged");
        AssertTrackedEntryState(expectedPreexistingState, preexistingEntry);
        AssertTrackedEntryState(expectedAddedClaimState, addedClaimEntry);
        Assert.True(addedClaimEntry.Property(claim => claim.Id).IsTemporary);
        Assert.Equal(temporaryAddedClaimId, addedClaim.Id);
        AssertTrackedEntryState(expectedDeletedClaimState, deletedClaimEntry);

        // The failed operation must leave this SAME context usable. A save on
        // a second unrelated tracked row is the adversarial check: the caller's
        // pre-existing edit must persist with it, while stale target values or
        // a leaked Added audit must not be flushed.
        var later = await db.Users.SingleAsync(u => u.Id == laterId);
        later.DisplayName = "Saved after final concurrency failure";
        await db.SaveChangesAsync();

        using var fresh = injected.Services.CreateScope();
        fresh.ServiceProvider.GetRequiredService<TenantContext>().Resolve(accountId);
        var freshDb = fresh.ServiceProvider.GetRequiredService<AppDbContext>();
        var persisted = await freshDb.Users.AsNoTracking().SingleAsync(u => u.Id == targetId);
        Assert.Equal(oldEmail, persisted.Email);
        Assert.Equal(oldUserName, persisted.UserName);
        Assert.Equal(oldSecurityStamp, persisted.SecurityStamp);
        Assert.Equal(oldConcurrencyStamp, persisted.ConcurrencyStamp);
        Assert.Equal(oldEpoch, persisted.CredentialEpoch);
        Assert.Equal(pendingDisplayName, persisted.DisplayName);
        Assert.Equal("Pending before email change",
            await freshDb.Users.Where(u => u.Id == preexistingId).Select(u => u.DisplayName).SingleAsync());
        Assert.Equal("Saved after final concurrency failure",
            await freshDb.Users.Where(u => u.Id == laterId).Select(u => u.DisplayName).SingleAsync());
        Assert.True(await freshDb.UserClaims.AnyAsync(claim => claim.ClaimType == addedClaimType));
        Assert.False(await freshDb.UserClaims.AnyAsync(claim => claim.Id == deletedClaimId));
        Assert.DoesNotContain(await freshDb.AuditEvents.AsNoTracking()
                .Where(audit => audit.EntityId == targetId).ToListAsync(),
            audit => audit.Action == "User.EmailChanged");
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
    public async Task ActorDemotedWhileHttpRequestIsQueued_Is403AuthForbidden_AndTargetIsUnchanged()
    {
        var (accountId, actor, actorId) = await SeedFarmAsync();
        await factory.SeedUserAsync(accountId, Unique("co-owner"), Roles.Owner);
        var target = Unique("target");
        await factory.SeedUserAsync(accountId, target, "Manager");
        var targetId = await UserIdAsync(accountId, target);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(actor));
        var proof = await StepUpAsync(actor);
        var changed = Unique("changed");
        var (db, tx, pid) = await FenceAccountAsync(accountId);
        await using var _ = db;
        await using var __ = tx;

        // The HTTP request validates and consumes its one-use proof before it
        // parks on the account fence. Demote the actor while it is queued so
        // only the transaction-local authorization re-read can reject it.
        var change = Task.Run(() => InvokeHttpAsync(client, targetId, changed, proof));
        Assert.True(await factory.WaitUntilDoneOrBlockedAsync(change, pid));
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM "AspNetUserRoles"
            WHERE "UserId" = {actorId}
              AND "RoleId" IN (SELECT "Id" FROM "AspNetRoles" WHERE "Name" = {Roles.Owner})
            """);
        await tx.CommitAsync();

        var response = await change;
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("Auth.Forbidden",
            (await response.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);
        Assert.Equal(target, await factory.WithTenantScopeAsync(accountId,
            scoped => scoped.Users.Where(u => u.Id == targetId).Select(u => u.Email).SingleAsync()));
    }

    [Fact]
    public void IsUserEmailConflict_AcceptsOnlyTheTwoNamedUniqueConstraints()
    {
        // The email-change/account-lock race above intentionally exercises the
        // friendly validator layer. A create-vs-change race could reach this
        // database layer, but there is no deterministic fence that makes the
        // 23505 occur specifically inside UpdateSecurityStampAsync without
        // depending on PostgreSQL scheduling. This direct discriminator test
        // therefore pins the exact SQLSTATE/constraint mapping instead of a
        // false-green race whose asserted layer is accidental.
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
