using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using Cluckwork.Api.Endpoints.Auth;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Users;
using Cluckwork.Domain.Accounts;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Cluckwork.Api.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public sealed class AccessAuthorizationContractTests(CluckworkWebApplicationFactory factory)
{
    [Fact]
    public async Task ScopedActorAndLookupShareStateAndRemainIndependent()
    {
        using var first = factory.Services.CreateScope();
        using var second = factory.Services.CreateScope();
        var actor = first.ServiceProvider.GetRequiredService<CurrentUserContext>();
        Assert.Same(actor, first.ServiceProvider.GetRequiredService<ICurrentUser>());
        Assert.NotSame(actor, second.ServiceProvider.GetRequiredService<ICurrentUser>());
        actor.Resolve(Guid.NewGuid(), "actor@test.local", [Roles.Manager]);
        Assert.False(second.ServiceProvider.GetRequiredService<ICurrentUser>().IsResolved);
        var lookup = first.ServiceProvider.GetRequiredService<IAccessLookup>();
        Assert.NotSame(lookup, second.ServiceProvider.GetRequiredService<IAccessLookup>());
        await Assert.ThrowsAsync<InvalidOperationException>(() => lookup.GetAssignedFlocksAsync(actor.UserId));
    }

    [Fact]
    public async Task AssignmentReadObservesIndependentCommitInTheSameScope()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"fresh-{Guid.NewGuid():N}@test.local");
        var flock = await factory.SeedFlockAsync(accountId, Guid.NewGuid());
        var email = $"worker-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, email, (string?)null);
        var worker = await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var id = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
            db.UserRoleAssignments.Add(UserRoleAssignment.Create(Guid.NewGuid(), accountId, id, null, null, flock));
            await db.SaveChangesAsync();
            return id;
        });
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Resolve(accountId);
        scope.ServiceProvider.GetRequiredService<CurrentUserContext>().Resolve(worker, email);
        var guard = scope.ServiceProvider.GetRequiredService<IFlockScopeGuard>();
        var foreignFlock = Guid.NewGuid();
        Assert.False((await guard.CheckAsync(foreignFlock)).IsSuccess);
        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync();
        await using var remove = new NpgsqlCommand("DELETE FROM \"UserRoleAssignments\" WHERE \"UserId\" = @user", connection);
        remove.Parameters.AddWithValue("user", worker);
        Assert.Equal(1, await remove.ExecuteNonQueryAsync());
        Assert.True((await guard.CheckAsync(foreignFlock)).IsSuccess,
            "zero committed assignment rows must grant unrestricted scope after an independent deletion");
    }

    [Fact]
    public async Task AssignmentReadFailurePropagatesInsteadOfGrantingUnrestrictedScope()
    {
        var commands = new AdmissionCommands { FailAssignments = true };
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(commands))));
        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Resolve(Guid.NewGuid());
        scope.ServiceProvider.GetRequiredService<CurrentUserContext>().Resolve(Guid.NewGuid(), "worker@test.local");
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<IFlockScopeGuard>().CheckAsync(Guid.NewGuid()));
        Assert.Equal("assignment read failed", failure.Message);
    }

    [Fact]
    public async Task MissingAssignmentProofReadsNoTargetsForKnownOrUnknownUsers()
    {
        var commands = new AdmissionCommands();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(commands))));
        var email = $"proof-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var flock = await factory.SeedFlockAsync(accountId, Guid.NewGuid());
        var client = host.CreateClient(TestHarness.Cookieless(factory));
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", await factory.LoginForAccessTokenAsync(email));
        var workerEmail = $"worker-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, workerEmail, (string?)null);
        var worker = await factory.WithTenantScopeAsync(accountId, db =>
            db.Users.Where(u => u.Email == workerEmail).Select(u => u.Id).SingleAsync());
        commands.Recording = true;
        foreach (var target in new[] { worker, Guid.NewGuid() })
        {
            var response = await client.PostWithKeyAsync($"/api/v1/users/{target}/flock-assignments",
                Guid.NewGuid().ToString(), new { flockId = flock });
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
            Assert.Equal("Identity.StepUpRequired", problem!.Title);
        }
        Assert.Empty(commands.TargetReads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AssignmentAdmissionRefusesDisabledAndMultipleRoleTargets(bool disabled)
    {
        var email = $"admit-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var flock = await factory.SeedFlockAsync(accountId, Guid.NewGuid());
        var targetEmail = $"target-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, targetEmail, (string?)null);
        Guid targetId;
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantContext>().Resolve(accountId);
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var target = (await users.FindByEmailAsync(targetEmail))!;
            targetId = target.Id;
            if (disabled)
            {
                target.DisabledAt = DateTimeOffset.UtcNow;
                Assert.True((await users.UpdateAsync(target)).Succeeded);
            }
            else
                Assert.True((await users.AddToRolesAsync(target, [Roles.ReadOnly, Roles.Manager])).Succeeded);
            var listed = await scope.ServiceProvider.GetRequiredService<IIdentityProvider>().ListUsersAsync(accountId);
            Assert.Equal(disabled ? "Worker" : Roles.Manager, listed.Single(u => u.Id == targetId).Role);
        }
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        var stepUp = await client.PostAsJsonAsync("/api/v1/auth/step-up", new { password = TestHarness.Password });
        stepUp.EnsureSuccessStatusCode();
        var proof = (await stepUp.Content.ReadFromJsonAsync<Proof>())!;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/users/{targetId}/flock-assignments")
        {
            Content = JsonContent.Create(new { flockId = flock }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Headers.Add(AuthEndpoints.StepUpHeaderName, proof.Token);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("Users.FlockAssignmentsWorkerOnly", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, await factory.WithTenantScopeAsync(accountId, db =>
            db.UserRoleAssignments.CountAsync(a => a.UserId == targetId)));
    }

    private sealed record Proof(string Token);

    private sealed class AdmissionCommands : DbCommandInterceptor
    {
        public bool Recording { get; set; }
        public bool FailAssignments { get; init; }
        public ConcurrentQueue<string> TargetReads { get; } = new();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            var sql = command.CommandText;
            if (FailAssignments && sql.Contains("FROM \"UserRoleAssignments\"", StringComparison.Ordinal))
                throw new InvalidOperationException("assignment read failed");
            if (Recording && !sql.Contains("\"AccountIsActive\"", StringComparison.Ordinal)
                && (sql.Contains("FROM \"AspNetUsers\"", StringComparison.Ordinal)
                    || sql.Contains("FROM \"AspNetUserRoles\"", StringComparison.Ordinal)))
                TargetReads.Enqueue(sql);
            return ValueTask.FromResult(result);
        }
    }
}
