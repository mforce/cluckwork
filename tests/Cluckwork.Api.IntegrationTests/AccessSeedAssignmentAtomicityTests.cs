using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Cluckwork.Domain.Modules.Farm.Accounts;
using Cluckwork.Domain.Modules.Farm.Contracts;
using Cluckwork.Infrastructure.Modules.Access.Identity;
using Cluckwork.Infrastructure.Persistence;
using Cluckwork.Infrastructure.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Identity;

namespace Cluckwork.Api.IntegrationTests;

public sealed class AccessSeedAuditFailureFactory : CluckworkWebApplicationFactory
{
    private readonly string castPassword = $"Aa1!{Guid.NewGuid():N}";

    public sealed class AssignmentActor
    {
        public Guid? UserId { get; set; }
        public IReadOnlyList<string> Roles { get; set; } = [];
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Simulation:CastPassword", castPassword);
        builder.UseSetting("Simulation:HistoryDays", "12");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IAuditWriter>();
            services.AddScoped<AuditWriter>();
            services.AddSingleton<AssignmentActor>();
            services.AddScoped<IAuditWriter, AssignmentAuditFailure>();
        });
    }

    private sealed class AssignmentAuditFailure(
        AuditWriter inner, CurrentUserContext actor, AssignmentActor observation) : IAuditWriter
    {
        public Task WriteAsync(string action, string entityType, Guid entityId,
            string? reason = null, object? details = null, CancellationToken ct = default)
        {
            if (action != AuditActions.UserFlockAssign)
                return inner.WriteAsync(action, entityType, entityId, reason, details, ct);
            observation.UserId = actor.UserId;
            observation.Roles = actor.Roles.ToArray();
            throw new InvalidOperationException("assignment audit failed");
        }
    }
}

public sealed class AccessSeedAssignmentAtomicityTests(AccessSeedAuditFailureFactory factory)
    : IClassFixture<AccessSeedAuditFailureFactory>
{
    [Fact]
    public async Task FailedAssignmentAuditLeavesNoCommittedAssignmentOrCompletionSignal()
    {
        var disabled = new Guid("00000000-0000-0000-0000-000000000001");
        var selected = new Guid("00000000-0000-0000-0000-000000000002");
        var later = new Guid("ffffffff-ffff-ffff-ffff-ffffffffffff");
        foreach (var (id, isDisabled) in new[] { (disabled, true), (selected, false), (later, false) })
        {
            using var create = factory.Services.CreateScope();
            create.ServiceProvider.GetRequiredService<TenantContext>().Resolve(SeedDefaults.AccountId);
            var users = create.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser
            {
                Id = id,
                AccountId = SeedDefaults.AccountId,
                Email = $"owner-{id:N}@test.local",
                UserName = $"owner-{id:N}@test.local",
                DisabledAt = isDisabled ? DateTimeOffset.UtcNow : null,
            };
            Assert.True((await users.CreateAsync(user, TestHarness.Password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, Roles.Owner)).Succeeded);
            if (id == selected)
                Assert.True((await users.AddToRoleAsync(user, Roles.ReadOnly)).Succeeded);
        }
        using var scope = factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<SimulationDataSeeder>().SeedAsync();
        Assert.Equal(SeedStatus.Failed, result.Status);
        Assert.Contains("assignment audit failed", result.Message);
        var actor = factory.Services.GetRequiredService<AccessSeedAuditFailureFactory.AssignmentActor>();
        Assert.Equal(selected, actor.UserId);
        Assert.Equal(new[] { Roles.Owner, Roles.ReadOnly }.Order(StringComparer.Ordinal),
            actor.Roles.Order(StringComparer.Ordinal));
        using var verify = factory.Services.CreateScope();
        verify.ServiceProvider.GetRequiredService<TenantContext>().Resolve(SeedDefaults.AccountId);
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.UserRoleAssignments.CountAsync());
        Assert.Null((await db.SimulationSeedStates.SingleAsync(s => s.AccountId == SeedDefaults.AccountId)).CompletedAtUtc);
    }
}
