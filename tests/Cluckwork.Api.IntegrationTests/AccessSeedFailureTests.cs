using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Domain.Modules.Farm.Accounts;
using Cluckwork.Domain.Modules.Farm.Contracts;
using Cluckwork.Infrastructure.Modules.Access.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cluckwork.Api.IntegrationTests;

public sealed class AccessSeedOwnerFailureFactory : CluckworkWebApplicationFactory
{
    public sealed class OwnerReadFailure
    {
        public bool Throw { get; set; }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Simulation:CastPassword", $"Aa1!{Guid.NewGuid():N}");
        builder.UseSetting("Simulation:HistoryDays", "12");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IAccessSeedLookup>();
            services.AddSingleton<OwnerReadFailure>();
            services.AddScoped<AccessSeedLookup>();
            services.AddScoped<IAccessSeedLookup, FailedOwnerRead>();
        });
    }

    private sealed class FailedOwnerRead(AccessSeedLookup inner, OwnerReadFailure failure) : IAccessSeedLookup
    {
        public Task<AccessActor?> FindUserByEmailAsync(Guid accountId, string email, CancellationToken ct = default) =>
            inner.FindUserByEmailAsync(accountId, email, ct);

        public Task<IReadOnlyList<AccessUserSummary>> ListUsersInRoleAsync(
            Guid accountId, string role, CancellationToken ct = default) =>
            inner.ListUsersInRoleAsync(accountId, role, ct);

        public Task<bool> OwnerRoleExistsAsync(CancellationToken ct = default) => inner.OwnerRoleExistsAsync(ct);

        public Task<int> CountUsersAsync(Guid accountId, CancellationToken ct = default) =>
            inner.CountUsersAsync(accountId, ct);

        public Task<AccessActor?> GetActorAsync(Guid accountId, Guid userId, CancellationToken ct = default) =>
            failure.Throw ? throw new InvalidOperationException("owner read failed") : Task.FromResult<AccessActor?>(null);
    }
}

public sealed class AccessSeedOwnerFailureTests(AccessSeedOwnerFailureFactory factory)
    : IClassFixture<AccessSeedOwnerFailureFactory>
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MissingOrFailedOwnerReadNeverResolvesAnActorOrCertifiesFixtures(bool simulation, bool throwRead)
    {
        await factory.SeedUserAsync(SeedDefaults.AccountId, $"missing-owner-{Guid.NewGuid():N}@test.local", Roles.Owner);
        factory.Services.GetRequiredService<AccessSeedOwnerFailureFactory.OwnerReadFailure>().Throw = throwRead;
        using var scope = factory.Services.CreateScope();
        async Task<SeedResult> Seed() => simulation
            ? await scope.ServiceProvider.GetRequiredService<SimulationDataSeeder>().SeedAsync()
            : await scope.ServiceProvider.GetRequiredService<DemoDataSeeder>().SeedAsync();
        if (throwRead && !simulation)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(Seed);
            Assert.Equal("owner read failed", error.Message);
        }
        else
        {
            var result = await Seed();
            Assert.Equal(SeedStatus.Failed, result.Status);
            Assert.Contains(throwRead ? "owner read failed" : "selected Owner cannot be read back", result.Message);
        }
        Assert.False(scope.ServiceProvider.GetRequiredService<CurrentUserContext>().IsResolved);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.Flocks.IgnoreQueryFilters().CountAsync());
        Assert.Equal(0, await db.SimulationSeedStates.IgnoreQueryFilters().CountAsync());
    }
}

public sealed class AccessSeedValidationFactory : CluckworkWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Simulation:CastPassword", $"Aa1!{Guid.NewGuid():N}");
        builder.UseSetting("Simulation:HistoryDays", "12");
    }
}

public sealed class AccessSeedValidationTests(AccessSeedValidationFactory factory)
    : IClassFixture<AccessSeedValidationFactory>
{
    [Fact]
    public async Task PollutedFixtureFailsExactValidationWithoutCompletionCertification()
    {
        await factory.SeedUserAsync(SeedDefaults.AccountId, $"validation-owner-{Guid.NewGuid():N}@test.local", Roles.Owner);
        await factory.SeedFlockAsync(SeedDefaults.AccountId, Guid.NewGuid());
        using var scope = factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<SimulationDataSeeder>().SeedAsync();
        Assert.Equal(SeedStatus.Failed, result.Status);
        Assert.Contains("flocks", result.Message, StringComparison.OrdinalIgnoreCase);
        using var verify = factory.Services.CreateScope();
        verify.ServiceProvider.GetRequiredService<TenantContext>().Resolve(SeedDefaults.AccountId);
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Null((await db.SimulationSeedStates.SingleAsync()).CompletedAtUtc);
    }
}
