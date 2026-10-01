namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Auditing;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// #500 — IAuditWriter fails closed on an unresolved ACTOR, symmetrically with
// the unresolved-TENANT guard it has had since #94.
//
// What this replaces: a silent fallback that stamped ActorEmail =
// "(unresolved)" for any caller that had not resolved a user. Only non-HTTP
// callers could reach it (auth precedes tenant resolution on every route), so
// in practice it was the seeders — and once #494 rendered provenance on five
// screens, ~256 demo rows read "Created by (unresolved)".
[Collection(IntegrationCollection.Name)]
public sealed class AuditActorTests(CluckworkWebApplicationFactory factory)
{
    private const string Action = "Flock.Create";

    // Tenant resolved, actor deliberately not. Both guards live in the same
    // method, so a test that only asserted "it throws" would pass on the
    // TENANT guard firing and prove nothing about the actor one — hence the
    // assertion on the message naming the actor.
    [Fact]
    public async Task WriteAsync_WithUnresolvedActor_Throws()
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Resolve(SeedDefaults.AccountId);
        var audit = scope.ServiceProvider.GetRequiredService<IAuditWriter>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => audit.WriteAsync(Action, "Flock", Guid.NewGuid()));

        Assert.Contains("actor", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // Fails closed, not half-closed. Asserted against the CHANGE TRACKER rather
    // than the table: AuditWriter never calls SaveChangesAsync (it appends to
    // the caller's unit of work), so a guard moved BELOW the AddAsync would
    // leave the table empty too and a table-based assertion could not tell the
    // difference.
    [Fact]
    public async Task WriteAsync_WithUnresolvedActor_AddsNothingToTheChangeTracker()
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Resolve(SeedDefaults.AccountId);
        var audit = scope.ServiceProvider.GetRequiredService<IAuditWriter>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => audit.WriteAsync(Action, "Flock", Guid.NewGuid()));

        Assert.Empty(db.ChangeTracker.Entries<AuditEvent>());
    }

    // The contract one-shot CLI verbs depend on: a caller with no human
    // actor declares WHICH non-person it is, and gets a row with that label and
    // an empty ActorUserId — deliberately chosen, where "(unresolved)" was
    // defaulted into by a fallback nobody could see.
    [Fact]
    public async Task WriteAsync_WithSystemActor_StampsTheLabelAndEmptyActorUserId()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"sys-{Guid.NewGuid():N}@test.local");
        var entityId = Guid.NewGuid();

        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantContext>().Resolve(accountId);
            scope.ServiceProvider.GetRequiredService<CurrentUserContext>()
                .ResolveSystemActor(SystemActors.BreakGlass);
            var audit = scope.ServiceProvider.GetRequiredService<IAuditWriter>();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            await audit.WriteAsync(AuditActions.UserBreakGlassReset, "User", entityId);
            await db.SaveChangesAsync();
        }

        var row = await factory.WithTenantScopeAsync(accountId, async db =>
            await db.AuditEvents.SingleAsync(e => e.EntityId == entityId));

        Assert.Equal(SystemActors.BreakGlass, row.ActorEmail);
        Assert.Equal(Guid.Empty, row.ActorUserId);
    }
}
