namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Features.Audit;
using Cluckwork.Domain.Auditing;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// #494 — "who created this, and who last changed it" derived from the
// append-only audit trail rather than from new columns on every aggregate.
// The lookup is raw SQL (DISTINCT ON), so it bypasses the EF tenant query
// filter: the AccountId predicate in that SQL is the only thing scoping it,
// which is what Provenance_IsScopedToTheTenant exists to hold in place.
[Collection(IntegrationCollection.Name)]
public sealed class AuditProvenanceTests(CluckworkWebApplicationFactory factory)
{
    private static readonly DateTimeOffset Base =
        new(2026, 5, 1, 8, 0, 0, TimeSpan.Zero);

    private async Task SeedEventsAsync(Guid accountId, params AuditEvent[] events) =>
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            db.AuditEvents.AddRange(events);
            await db.SaveChangesAsync();
        });

    // actorUserId defaults to a fresh id, so events are by DIFFERENT people
    // unless a test deliberately passes the same one. Identity is the user id,
    // never the email — the email is a snapshot label that can be re-pointed.
    private static AuditEvent Event(
        Guid accountId, Guid entityId, string action, string email, int minutesFromBase,
        string entityType = "Flock", Guid? actorUserId = null) =>
        AuditEvent.Create(
            Guid.NewGuid(), accountId, Base.AddMinutes(minutesFromBase),
            actorUserId ?? Guid.NewGuid(), email, action, entityType, entityId);

    private async Task<T> WithRepositoryAsync<T>(
        Guid accountId, Func<IAuditEventRepository, Task<T>> action)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Resolve(accountId);
        return await action(scope.ServiceProvider.GetRequiredService<IAuditEventRepository>());
    }

    // Never changed since creation: the trail's two ends are the SAME event, so
    // there is no change to report. Deciding that here rather than in the UI is
    // deliberate — only this layer can tell one event from two that happen to
    // share an instant (see Provenance_WhenTwoEventsShareAnInstant...).
    [Fact]
    public async Task Provenance_WithOneEvent_ReportsNoChange()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"u-{Guid.NewGuid():N}@test.local");
        var entityId = Guid.NewGuid();
        await SeedEventsAsync(accountId, Event(accountId, entityId, "Flock.Create", "ana@farm.test", 0));

        var result = await WithRepositoryAsync(accountId, repo =>
            repo.GetProvenanceAsync("Flock", [entityId]));

        var provenance = result[entityId];
        Assert.Equal("ana@farm.test", provenance.CreatedByEmail);
        Assert.Equal(Base, provenance.CreatedAtUtc);
        Assert.Null(provenance.LastChangedByEmail);
        Assert.Null(provenance.LastChangedAtUtc);
    }

    // A record that predates #494 has no ".Create" event, only corrections.
    // Naming its first corrector as its author would be a claim the trail does
    // not support — an outright false attribution, not merely missing data.
    //
    // But refusing to invent a creator is a separate decision from discarding a
    // change we DO have attribution for, and an earlier revision made both at
    // once by keying the whole result off the creation event: a flock archived
    // by cy yesterday rendered completely blank (adversarial review of PR #503).
    // Now the two halves are independent — no creator, real last change.
    [Fact]
    public async Task Provenance_ForALegacyRecordWithOnlyCorrections_NamesNoCreatorButKeepsTheChange()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"u-{Guid.NewGuid():N}@test.local");
        var entityId = Guid.NewGuid();
        await SeedEventsAsync(accountId,
            Event(accountId, entityId, "Flock.Update", "bo@farm.test", 30),
            Event(accountId, entityId, "Flock.Archive", "cy@farm.test", 90));

        var result = await WithRepositoryAsync(accountId, repo =>
            repo.GetProvenanceAsync("Flock", [entityId]));

        var provenance = result[entityId];
        Assert.Null(provenance.CreatedByEmail);
        Assert.Null(provenance.CreatedAtUtc);
        Assert.Equal("cy@farm.test", provenance.LastChangedByEmail);
        Assert.Equal(Base.AddMinutes(90), provenance.LastChangedAtUtc);
    }

    // A legacy record whose ONLY event is a drafting action — no ".Create", so
    // the LEFT JOIN finds no creator and c."ActorUserId" is NULL.
    //
    // This is what makes IS NOT DISTINCT FROM load-bearing, which it was NOT
    // before the result stopped being keyed off the creation event. With a plain
    // "=", NULL comparison yields SQL NULL, NOT (drafting AND NULL) is NULL, and
    // WHERE drops the row entirely — so a real, attributable change silently
    // disappears rather than merely failing to be excluded (conventions review of
    // PR #503, which caught the comment claiming this could not happen).
    [Fact]
    public async Task Provenance_ForALegacyRecordWhoseOnlyEventIsDrafting_StillReportsIt()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"u-{Guid.NewGuid():N}@test.local");
        var entityId = Guid.NewGuid();
        await SeedEventsAsync(accountId,
            Event(accountId, entityId, "DailyEntry.Submit", "bo@farm.test", 30, "DailyEntry"));

        var result = await WithRepositoryAsync(accountId, repo =>
            repo.GetProvenanceAsync("DailyEntry", [entityId]));

        var provenance = result[entityId];
        Assert.Null(provenance.CreatedByEmail);
        Assert.Equal("bo@farm.test", provenance.LastChangedByEmail);
        Assert.Equal(Base.AddMinutes(30), provenance.LastChangedAtUtc);
    }

    [Fact]
    public async Task Provenance_WithSeveralEvents_ReportsTheEarliestAndTheLatest()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"u-{Guid.NewGuid():N}@test.local");
        var entityId = Guid.NewGuid();
        // Deliberately inserted out of order: the SQL orders, not the insert.
        await SeedEventsAsync(accountId,
            Event(accountId, entityId, "Flock.Update", "bo@farm.test", 30),
            Event(accountId, entityId, "Flock.Create", "ana@farm.test", 0),
            Event(accountId, entityId, "Flock.Archive", "cy@farm.test", 90));

        var result = await WithRepositoryAsync(accountId, repo =>
            repo.GetProvenanceAsync("Flock", [entityId]));

        var provenance = result[entityId];
        Assert.Equal("ana@farm.test", provenance.CreatedByEmail);
        Assert.Equal(Base, provenance.CreatedAtUtc);
        Assert.Equal("cy@farm.test", provenance.LastChangedByEmail);
        Assert.Equal(Base.AddMinutes(90), provenance.LastChangedAtUtc);
    }

    // Two events can share an instant, and their relative order is then
    // UNKNOWABLE: AuditWriter mints a random v4 Guid, so an Id tiebreaker sorts
    // arbitrarily rather than chronologically (codex review of PR #503 — an
    // earlier revision of this test picked its expected actor by that ordering
    // and passed only ~half the time).
    //
    // So creation is identified by its ACTION, not by its position in the
    // trail. That is what makes this deterministic: whichever event sorts
    // first, the ".Create" one is the creation and the other is the change.
    [Fact]
    public async Task Provenance_WhenTwoEventsShareAnInstant_NamesTheCreatorByAction()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"u-{Guid.NewGuid():N}@test.local");
        var entityId = Guid.NewGuid();
        await SeedEventsAsync(accountId,
            Event(accountId, entityId, "Flock.Create", "ana@farm.test", 0),
            Event(accountId, entityId, "Flock.Update", "bo@farm.test", 0));

        var result = await WithRepositoryAsync(accountId, repo =>
            repo.GetProvenanceAsync("Flock", [entityId]));

        var provenance = result[entityId];
        Assert.Equal("ana@farm.test", provenance.CreatedByEmail);
        // Distinct events, so this is a real change and must be reported.
        Assert.NotNull(provenance.LastChangedByEmail);
        Assert.Equal("bo@farm.test", provenance.LastChangedByEmail);
    }

    // --- submitting your own draft is not a change -------------------------
    //
    // Saving a draft and submitting it are two events, but one act: the person
    // wrote the day's numbers down and made them official, changing nothing in
    // between. Reporting "last changed by ana" there is noise that reads like
    // somebody corrected ana's work.
    //
    // So the rule is NOT "the latest event after the creation" — it is "the
    // latest event that is neither the creation NOR a Submit by the creator".
    // Stated as an exclusion rather than as a check on the last event, which is
    // what keeps the three tests below from contradicting each other.

    [Fact]
    public async Task Provenance_WhenTheCreatorSubmitsTheirOwnDraft_ReportsNoChange()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"u-{Guid.NewGuid():N}@test.local");
        var entityId = Guid.NewGuid();
        var ana = Guid.NewGuid();
        await SeedEventsAsync(accountId,
            Event(accountId, entityId, "DailyEntry.Create", "ana@farm.test", 0, "DailyEntry", ana),
            Event(accountId, entityId, "DailyEntry.Submit", "ana@farm.test", 5, "DailyEntry", ana));

        var result = await WithRepositoryAsync(accountId, repo =>
            repo.GetProvenanceAsync("DailyEntry", [entityId]));

        var provenance = result[entityId];
        Assert.Equal("ana@farm.test", provenance.CreatedByEmail);
        Assert.Equal(Base, provenance.CreatedAtUtc);
        Assert.Null(provenance.LastChangedByEmail);
        Assert.Null(provenance.LastChangedAtUtc);
    }

    // Suppressing the self-promotion from "last changed BY" must not also lose
    // WHEN it happened. The promotion is the instant stock is minted, and it is
    // recorded nowhere else on the record's own page: DailyEntry has no
    // SubmittedAt field, only LockedAtUtc. Draft Monday, submit Friday — before
    // this, the row read "created Monday" and Friday existed only in the admin
    // audit log (adversarial review of PR #503).
    [Fact]
    public async Task Provenance_WhenTheCreatorSubmitsTheirOwnDraft_StillReportsWhen()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"u-{Guid.NewGuid():N}@test.local");
        var entityId = Guid.NewGuid();
        var ana = Guid.NewGuid();
        await SeedEventsAsync(accountId,
            Event(accountId, entityId, "DailyEntry.Create", "ana@farm.test", 0, "DailyEntry", ana),
            Event(accountId, entityId, "DailyEntry.Submit", "ana@farm.test", 5760, "DailyEntry", ana));

        var result = await WithRepositoryAsync(accountId, repo =>
            repo.GetProvenanceAsync("DailyEntry", [entityId]));

        var provenance = result[entityId];
        Assert.Null(provenance.LastChangedByEmail);
        // Four days later — the moment the eggs entered stock.
        Assert.Equal(Base.AddMinutes(5760), provenance.MadeOfficialAtUtc);
    }

    // Reported whoever promoted it, so the two pages never disagree about when
    // the record became official.
    [Fact]
    public async Task Provenance_WhenSomeoneElseSubmits_StillReportsWhenItBecameOfficial()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"u-{Guid.NewGuid():N}@test.local");
        var entityId = Guid.NewGuid();
        await SeedEventsAsync(accountId,
            Event(accountId, entityId, "DailyEntry.Create", "ana@farm.test", 0, "DailyEntry"),
            Event(accountId, entityId, "DailyEntry.Submit", "bo@farm.test", 5, "DailyEntry"));

        var result = await WithRepositoryAsync(accountId, repo =>
            repo.GetProvenanceAsync("DailyEntry", [entityId]));

        var provenance = result[entityId];
        Assert.Equal("bo@farm.test", provenance.LastChangedByEmail);
        Assert.Equal(Base.AddMinutes(5), provenance.MadeOfficialAtUtc);
    }

    // The promotion lookup is a THIRD query with its own tenant predicate, so it
    // needs its own guard — the lesson from the last-change query, where two
    // predicates sat unguarded because every test used distinct ids per account.
    [Fact]
    public async Task Provenance_MadeOfficial_IsScopedToTheTenant()
    {
        var mine = await factory.SeedAccountWithUserAsync($"u-{Guid.NewGuid():N}@test.local");
        var theirs = await factory.SeedAccountWithUserAsync($"u-{Guid.NewGuid():N}@test.local");
        var entityId = Guid.NewGuid();

        // Mine is still a draft. Theirs, sharing the id, has been submitted.
        await SeedEventsAsync(mine,
            Event(mine, entityId, "DailyEntry.Create", "ana@farm.test", 0, "DailyEntry"));
        await SeedEventsAsync(theirs,
            Event(theirs, entityId, "DailyEntry.Create", "rival@other.test", 1, "DailyEntry"),
            Event(theirs, entityId, "DailyEntry.Submit", "rival@other.test", 2, "DailyEntry"));

        var result = await WithRepositoryAsync(mine, repo =>
            repo.GetProvenanceAsync("DailyEntry", [entityId]));

        // Drop the promotion query's AccountId and my draft would claim to have
        // been made official — by an event belonging to another farm.
        Assert.Null(result[entityId].MadeOfficialAtUtc);
    }

    // A record with no promotion step at all — flocks, grades, expenses — and a
    // draft still awaiting one. Nothing to report, and no zero-date invented.
    [Fact]
    public async Task Provenance_WithNoPromotion_ReportsNoOfficialInstant()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"u-{Guid.NewGuid():N}@test.local");
        var entityId = Guid.NewGuid();
        await SeedEventsAsync(accountId,
            Event(accountId, entityId, "Flock.Create", "ana@farm.test", 0),
            Event(accountId, entityId, "Flock.Update", "bo@farm.test", 30));

        var result = await WithRepositoryAsync(accountId, repo =>
            repo.GetProvenanceAsync("Flock", [entityId]));

        Assert.Null(result[entityId].MadeOfficialAtUtc);
    }

    // The masked-co-author case. bo rewrites ana's draft and ana submits it: the
    // numbers that became stock are bo's, and before draft edits were recorded
    // this read "created by ana, never changed" — crediting ana with bo's work
    // (adversarial review of PR #503).
    //
    // ana's own submit is still hidden, because it is her own drafting. bo's
    // edit is not, because bo is not the creator.
    [Fact]
    public async Task Provenance_WhenSomeoneElseEditsTheDraft_NamesThemEvenIfTheCreatorSubmits()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"u-{Guid.NewGuid():N}@test.local");
        var entityId = Guid.NewGuid();
        var ana = Guid.NewGuid();
        await SeedEventsAsync(accountId,
            Event(accountId, entityId, "DailyEntry.Create", "ana@farm.test", 0, "DailyEntry", ana),
            Event(accountId, entityId, "DailyEntry.Update", "bo@farm.test", 2, "DailyEntry"),
            Event(accountId, entityId, "DailyEntry.Submit", "ana@farm.test", 5, "DailyEntry", ana));

        var result = await WithRepositoryAsync(accountId, repo =>
            repo.GetProvenanceAsync("DailyEntry", [entityId]));

        var provenance = result[entityId];
        Assert.Equal("ana@farm.test", provenance.CreatedByEmail);
        Assert.Equal("bo@farm.test", provenance.LastChangedByEmail);
        Assert.Equal(Base.AddMinutes(2), provenance.LastChangedAtUtc);
        // The submit still happened, and when it happened is still reported.
        Assert.Equal(Base.AddMinutes(5), provenance.MadeOfficialAtUtc);
    }

    // The other half of the same rule, and the reason it is keyed on the actor:
    // editing your OWN draft before submitting must stay quiet, or every entry
    // reads as having been corrected by the person who wrote it.
    [Fact]
    public async Task Provenance_WhenTheCreatorEditsTheirOwnDraft_ReportsNoChange()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"u-{Guid.NewGuid():N}@test.local");
        var entityId = Guid.NewGuid();
        var ana = Guid.NewGuid();
        await SeedEventsAsync(accountId,
            Event(accountId, entityId, "DailyEntry.Create", "ana@farm.test", 0, "DailyEntry", ana),
            Event(accountId, entityId, "DailyEntry.Update", "ana@farm.test", 2, "DailyEntry", ana),
            Event(accountId, entityId, "DailyEntry.Submit", "ana@farm.test", 5, "DailyEntry", ana));

        var result = await WithRepositoryAsync(accountId, repo =>
            repo.GetProvenanceAsync("DailyEntry", [entityId]));

        var provenance = result[entityId];
        Assert.Null(provenance.LastChangedByEmail);
        Assert.Null(provenance.LastChangedAtUtc);
    }

    // The case the whole option exists for: a worker writes the numbers, someone
    // else makes them official. Both people must be visible — that is the
    // accountability the submit step is for.
    [Fact]
    public async Task Provenance_WhenSomeoneElseSubmitsTheDraft_ReportsTheSubmitter()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"u-{Guid.NewGuid():N}@test.local");
        var entityId = Guid.NewGuid();
        await SeedEventsAsync(accountId,
            Event(accountId, entityId, "DailyEntry.Create", "ana@farm.test", 0, "DailyEntry"),
            Event(accountId, entityId, "DailyEntry.Submit", "bo@farm.test", 5, "DailyEntry"));

        var result = await WithRepositoryAsync(accountId, repo =>
            repo.GetProvenanceAsync("DailyEntry", [entityId]));

        var provenance = result[entityId];
        Assert.Equal("ana@farm.test", provenance.CreatedByEmail);
        Assert.Equal("bo@farm.test", provenance.LastChangedByEmail);
        Assert.Equal(Base.AddMinutes(5), provenance.LastChangedAtUtc);
    }

    // The suppression is keyed on the ACTION, not on the actor alone. Correcting
    // a locked entry is a stock-altering change and must always show, including
    // when the person correcting it is the one who created it — a blanket
    // "same actor" rule would silently swallow exactly that.
    [Fact]
    public async Task Provenance_WhenTheCreatorAdjustsTheirOwnEntry_StillReportsTheChange()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"u-{Guid.NewGuid():N}@test.local");
        var entityId = Guid.NewGuid();
        var ana = Guid.NewGuid();
        await SeedEventsAsync(accountId,
            Event(accountId, entityId, "DailyEntry.Create", "ana@farm.test", 0, "DailyEntry", ana),
            Event(accountId, entityId, "DailyEntry.Submit", "ana@farm.test", 5, "DailyEntry", ana),
            Event(accountId, entityId, "DailyEntry.Adjust", "ana@farm.test", 600, "DailyEntry", ana));

        var result = await WithRepositoryAsync(accountId, repo =>
            repo.GetProvenanceAsync("DailyEntry", [entityId]));

        var provenance = result[entityId];
        Assert.Equal("ana@farm.test", provenance.LastChangedByEmail);
        Assert.Equal(Base.AddMinutes(600), provenance.LastChangedAtUtc);
    }

    // Excluding the self-submit must not take anything else with it. Had the
    // rule been "look at the last event, and if it is a self-submit report
    // nothing", bo's edit would vanish — a real change, hidden. Not reachable
    // through today's handlers (draft re-saves write no event), so this pins the
    // SHAPE of the rule rather than a live path.
    [Fact]
    public async Task Provenance_WhenAnEarlierChangeByAnotherPersonPrecedesASelfSubmit_StillReportsIt()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"u-{Guid.NewGuid():N}@test.local");
        var entityId = Guid.NewGuid();
        var ana = Guid.NewGuid();
        await SeedEventsAsync(accountId,
            Event(accountId, entityId, "DailyEntry.Create", "ana@farm.test", 0, "DailyEntry", ana),
            Event(accountId, entityId, "DailyEntry.Adjust", "bo@farm.test", 2, "DailyEntry"),
            Event(accountId, entityId, "DailyEntry.Submit", "ana@farm.test", 5, "DailyEntry", ana));

        var result = await WithRepositoryAsync(accountId, repo =>
            repo.GetProvenanceAsync("DailyEntry", [entityId]));

        var provenance = result[entityId];
        Assert.Equal("bo@farm.test", provenance.LastChangedByEmail);
        Assert.Equal(Base.AddMinutes(2), provenance.LastChangedAtUtc);
    }

    // Sales orders take the identical shape: a draft order is created, then
    // CONFIRMED, and confirming is what allocates stock. So the same exclusion
    // covers it — the pattern is "an action that promotes a draft to official",
    // of which Submit and Confirm are the two instances.
    [Fact]
    public async Task Provenance_WhenTheCreatorConfirmsTheirOwnOrder_ReportsNoChange()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"u-{Guid.NewGuid():N}@test.local");
        var entityId = Guid.NewGuid();
        var ana = Guid.NewGuid();
        await SeedEventsAsync(accountId,
            Event(accountId, entityId, "SalesOrder.Create", "ana@farm.test", 0, "SalesOrder", ana),
            Event(accountId, entityId, "SalesOrder.Confirm", "ana@farm.test", 5, "SalesOrder", ana));

        var result = await WithRepositoryAsync(accountId, repo =>
            repo.GetProvenanceAsync("SalesOrder", [entityId]));

        var provenance = result[entityId];
        Assert.Equal("ana@farm.test", provenance.CreatedByEmail);
        Assert.Null(provenance.LastChangedByEmail);
        Assert.Null(provenance.LastChangedAtUtc);
    }

    [Fact]
    public async Task Provenance_WhenSomeoneElseConfirmsTheOrder_ReportsTheConfirmer()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"u-{Guid.NewGuid():N}@test.local");
        var entityId = Guid.NewGuid();
        await SeedEventsAsync(accountId,
            Event(accountId, entityId, "SalesOrder.Create", "ana@farm.test", 0, "SalesOrder"),
            Event(accountId, entityId, "SalesOrder.Confirm", "bo@farm.test", 5, "SalesOrder"));

        var result = await WithRepositoryAsync(accountId, repo =>
            repo.GetProvenanceAsync("SalesOrder", [entityId]));

        var provenance = result[entityId];
        Assert.Equal("ana@farm.test", provenance.CreatedByEmail);
        Assert.Equal("bo@farm.test", provenance.LastChangedByEmail);
        Assert.Equal(Base.AddMinutes(5), provenance.LastChangedAtUtc);
    }

    // Cancelling is not stock-altering — Cancel is Draft-only, so nothing has
    // been allocated yet — but it is TERMINAL, and a record that somebody killed
    // must say who. Not covered by the promotion exclusion: cancelling your own
    // draft is a real change, unlike confirming it.
    [Fact]
    public async Task Provenance_WhenTheCreatorCancelsTheirOwnOrder_StillReportsTheChange()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"u-{Guid.NewGuid():N}@test.local");
        var entityId = Guid.NewGuid();
        var ana = Guid.NewGuid();
        await SeedEventsAsync(accountId,
            Event(accountId, entityId, "SalesOrder.Create", "ana@farm.test", 0, "SalesOrder", ana),
            Event(accountId, entityId, "SalesOrder.Cancel", "ana@farm.test", 5, "SalesOrder", ana));

        var result = await WithRepositoryAsync(accountId, repo =>
            repo.GetProvenanceAsync("SalesOrder", [entityId]));

        var provenance = result[entityId];
        Assert.Equal("ana@farm.test", provenance.LastChangedByEmail);
        Assert.Equal(Base.AddMinutes(5), provenance.LastChangedAtUtc);
    }

    // The LAST-CHANGE query has its own tenant predicate, in two places — the
    // outer SELECT and the `creator` CTE — and Provenance_IsScopedToTheTenant
    // does NOT hold either one: it gives each account a distinct entityId, so
    // the foreign row is already gone before those predicates are consulted.
    // Both survived deletion against the whole suite (adversarial review of PR
    // #503), which made the comment claiming they were guarded a false
    // assurance rather than a guard.
    //
    // So this shares ONE entityId across two accounts. Not reachable in
    // production — ids are unique — but it is the only shape in which those
    // predicates do any work, and a guard that never exercises the line it
    // claims to protect is worse than none.
    //
    // Kills both: drop the outer predicate and the rival's later Adjust becomes
    // the last change; drop the CTE's predicate and the CTE picks the rival's
    // EARLIER Create as the creator, so ana's self-submit stops being
    // suppressed and surfaces as a change.
    [Fact]
    public async Task Provenance_LastChange_IsScopedToTheTenant()
    {
        var mine = await factory.SeedAccountWithUserAsync($"u-{Guid.NewGuid():N}@test.local");
        var theirs = await factory.SeedAccountWithUserAsync($"u-{Guid.NewGuid():N}@test.local");
        var entityId = Guid.NewGuid();
        var ana = Guid.NewGuid();
        var rival = Guid.NewGuid();

        await SeedEventsAsync(theirs,
            // Earlier than my creation, so an unscoped CTE would prefer it.
            Event(theirs, entityId, "DailyEntry.Create", "rival@other.test", -10, "DailyEntry", rival),
            // Later than my submit, so an unscoped outer query would prefer it.
            Event(theirs, entityId, "DailyEntry.Adjust", "rival@other.test", 9, "DailyEntry", rival));
        await SeedEventsAsync(mine,
            Event(mine, entityId, "DailyEntry.Create", "ana@farm.test", 0, "DailyEntry", ana),
            Event(mine, entityId, "DailyEntry.Submit", "ana@farm.test", 5, "DailyEntry", ana));

        var result = await WithRepositoryAsync(mine, repo =>
            repo.GetProvenanceAsync("DailyEntry", [entityId]));

        var provenance = result[entityId];
        Assert.Equal("ana@farm.test", provenance.CreatedByEmail);
        Assert.Null(provenance.LastChangedByEmail);
        Assert.Null(provenance.LastChangedAtUtc);
    }

    // The CTE that resolves the creator selects by ACTION, exactly as the outer
    // creation query does. Position cannot be substituted for it, so an event
    // that merely sorts earlier must not be mistaken for the creation.
    //
    // The fixture is deliberately unnatural — a correction dated before the
    // record exists — because that is the only arrangement in which position
    // and action disagree. It pins the mechanism, not a reachable sequence.
    [Fact]
    public async Task Provenance_TheCreatorForTheExclusion_IsFoundByActionNotByPosition()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"u-{Guid.NewGuid():N}@test.local");
        var entityId = Guid.NewGuid();
        var ana = Guid.NewGuid();
        await SeedEventsAsync(accountId,
            Event(accountId, entityId, "DailyEntry.Adjust", "bo@farm.test", -5, "DailyEntry"),
            Event(accountId, entityId, "DailyEntry.Create", "ana@farm.test", 0, "DailyEntry", ana),
            Event(accountId, entityId, "DailyEntry.Submit", "ana@farm.test", 5, "DailyEntry", ana));

        var result = await WithRepositoryAsync(accountId, repo =>
            repo.GetProvenanceAsync("DailyEntry", [entityId]));

        var provenance = result[entityId];
        Assert.Equal("ana@farm.test", provenance.CreatedByEmail);
        // ana's submit is suppressed as a self-promotion, so bo's correction is
        // the last change. Were the CTE to take the earliest event instead of
        // the ".Create" one, it would call bo the creator, ana's submit would
        // stop matching, and ana would surface here instead.
        Assert.Equal("bo@farm.test", provenance.LastChangedByEmail);
        Assert.Equal(Base.AddMinutes(-5), provenance.LastChangedAtUtc);
    }

    // Every member of the drafting set, not just the two a page-level test
    // happens to exercise. UpdateItem and RemoveItem could both be deleted from
    // that set with the FULL 1104-test suite still green, while their two
    // siblings were held — the "two of four covered" shape that reads as
    // complete (adversarial review of PR #503).
    [Fact]
    public async Task Provenance_WhenTheCreatorReworksTheirOwnOrderLines_ReportsNoChange()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"u-{Guid.NewGuid():N}@test.local");
        var entityId = Guid.NewGuid();
        var ana = Guid.NewGuid();
        await SeedEventsAsync(accountId,
            Event(accountId, entityId, "SalesOrder.Create", "ana@farm.test", 0, "SalesOrder", ana),
            Event(accountId, entityId, "SalesOrder.AddItem", "ana@farm.test", 1, "SalesOrder", ana),
            Event(accountId, entityId, "SalesOrder.UpdateItem", "ana@farm.test", 2, "SalesOrder", ana),
            Event(accountId, entityId, "SalesOrder.RemoveItem", "ana@farm.test", 3, "SalesOrder", ana),
            Event(accountId, entityId, "SalesOrder.Confirm", "ana@farm.test", 4, "SalesOrder", ana));

        var result = await WithRepositoryAsync(accountId, repo =>
            repo.GetProvenanceAsync("SalesOrder", [entityId]));

        var provenance = result[entityId];
        Assert.Equal("ana@farm.test", provenance.CreatedByEmail);
        // Assembling your own order is one act. Drop any member of the drafting
        // set and that member resurfaces here as "somebody changed your work".
        Assert.Null(provenance.LastChangedByEmail);
        Assert.Null(provenance.LastChangedAtUtc);
    }

    // The EntityType predicate appears FOUR times, and three of them survived
    // deletion against the whole suite — the same miss as the tenant predicate,
    // in the same file, found the same way. Provenance_IgnoresEventsOfAnother-
    // EntityType holds only the `created` query, because its single foreign
    // event is a ".Create" that the other queries exclude by action anyway.
    //
    // One id, four entity types, arranged so each unguarded predicate changes a
    // different answer:
    //   outer last-change  — a foreign Archive would become the last change
    //   creator CTE        — a foreign, EARLIER Create would win the creator slot,
    //                        un-suppressing ana's own submit
    //   promoted           — a foreign, EARLIER Confirm would become the instant
    //                        this entry supposedly became official
    [Fact]
    public async Task Provenance_EveryQueryIsScopedToTheEntityType()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"u-{Guid.NewGuid():N}@test.local");
        var entityId = Guid.NewGuid();
        var ana = Guid.NewGuid();
        var rival = Guid.NewGuid();
        await SeedEventsAsync(accountId,
            Event(accountId, entityId, "SalesOrder.Confirm", "rival@farm.test", -20, "SalesOrder", rival),
            Event(accountId, entityId, "Flock.Create", "rival@farm.test", -10, "Flock", rival),
            Event(accountId, entityId, "DailyEntry.Create", "ana@farm.test", 0, "DailyEntry", ana),
            Event(accountId, entityId, "DailyEntry.Submit", "ana@farm.test", 5, "DailyEntry", ana),
            Event(accountId, entityId, "Flock.Archive", "rival@farm.test", 90, "Flock", rival));

        var result = await WithRepositoryAsync(accountId, repo =>
            repo.GetProvenanceAsync("DailyEntry", [entityId]));

        var provenance = result[entityId];
        Assert.Equal("ana@farm.test", provenance.CreatedByEmail);
        Assert.Null(provenance.LastChangedByEmail);
        Assert.Equal(Base.AddMinutes(5), provenance.MadeOfficialAtUtc);
    }

    [Fact]
    public async Task Provenance_IsScopedToTheTenant()
    {
        var mine = await factory.SeedAccountWithUserAsync($"u-{Guid.NewGuid():N}@test.local");
        var theirs = await factory.SeedAccountWithUserAsync($"u-{Guid.NewGuid():N}@test.local");
        var myEntity = Guid.NewGuid();
        var theirEntity = Guid.NewGuid();
        await SeedEventsAsync(mine, Event(mine, myEntity, "Flock.Create", "ana@farm.test", 0));
        await SeedEventsAsync(theirs, Event(theirs, theirEntity, "Flock.Create", "rival@other.test", 0));

        var result = await WithRepositoryAsync(mine, repo =>
            repo.GetProvenanceAsync("Flock", [myEntity, theirEntity]));

        Assert.True(result.ContainsKey(myEntity));
        Assert.False(result.ContainsKey(theirEntity));
    }

    [Fact]
    public async Task Provenance_IgnoresEventsOfAnotherEntityType()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"u-{Guid.NewGuid():N}@test.local");
        // The same id under two entity types: only the asked-for type counts.
        var entityId = Guid.NewGuid();
        await SeedEventsAsync(accountId,
            Event(accountId, entityId, "Expense.Create", "bo@farm.test", 0, entityType: "Expense"));

        var result = await WithRepositoryAsync(accountId, repo =>
            repo.GetProvenanceAsync("Flock", [entityId]));

        Assert.Empty(result);
    }

    [Fact]
    public async Task Provenance_ForAnIdWithNoEvents_IsAbsentRatherThanBlank()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"u-{Guid.NewGuid():N}@test.local");
        var known = Guid.NewGuid();
        var untouched = Guid.NewGuid();
        await SeedEventsAsync(accountId, Event(accountId, known, "Flock.Create", "ana@farm.test", 0));

        var result = await WithRepositoryAsync(accountId, repo =>
            repo.GetProvenanceAsync("Flock", [known, untouched]));

        Assert.True(result.ContainsKey(known));
        Assert.False(result.ContainsKey(untouched));
    }

    [Fact]
    public async Task Provenance_WithNoIds_ReturnsEmptyWithoutQuerying()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"u-{Guid.NewGuid():N}@test.local");

        var result = await WithRepositoryAsync(accountId, repo =>
            repo.GetProvenanceAsync("Flock", []));

        Assert.Empty(result);
    }

    // --- over the wire -----------------------------------------------------
    //
    // One per extended list endpoint: the point of #494 is that provenance
    // reaches the record's OWN page without a second call, so each of these
    // asserts the creating user's email came back on the list row itself.

    private sealed record ProvenanceRowDto(
        Guid Id, string? CreatedByEmail, DateTimeOffset? CreatedAtUtc,
        string? LastChangedByEmail, DateTimeOffset? LastChangedAtUtc,
        DateTimeOffset? MadeOfficialAtUtc);
    private sealed record ExpenseListDto(List<ProvenanceRowDto> Items);
    private sealed record CreatedDto(Guid Id);

    private async Task<(HttpClient Client, string Email)> AuthedAsync()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);
        return (factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email)), email);
    }

    private static async Task<Guid> CreatedIdAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CreatedDto>())!.Id;
    }

    private static void AssertCreatedBy(ProvenanceRowDto row, string email)
    {
        Assert.Equal(email, row.CreatedByEmail);
        Assert.NotNull(row.CreatedAtUtc);
        // Never changed since creation: the trail's earliest and latest event
        // are the same one, so the server reports no change at all.
        Assert.Null(row.LastChangedByEmail);
        Assert.Null(row.LastChangedAtUtc);
    }

    [Fact]
    public async Task FlockList_CarriesProvenance()
    {
        var (client, email) = await AuthedAsync();
        var id = await CreatedIdAsync(await client.PostWithKeyAsync(
            "/api/v1/flocks", Guid.NewGuid().ToString(),
            new { name = "Barn A", breed = "ISA Brown", placementDate = "2026-01-01", initialCount = 200 }));

        var rows = await client.GetFromJsonAsync<List<ProvenanceRowDto>>("/api/v1/flocks");

        AssertCreatedBy(rows!.Single(r => r.Id == id), email);
    }

    [Fact]
    public async Task EggGradeList_CarriesProvenance()
    {
        var (client, email) = await AuthedAsync();
        var id = await CreatedIdAsync(await client.PostWithKeyAsync(
            "/api/v1/egg-grades", Guid.NewGuid().ToString(),
            new { name = "Jumbo", gradeType = "Custom", sortOrder = 7, isSaleable = true }));

        var rows = await client.GetFromJsonAsync<List<ProvenanceRowDto>>("/api/v1/egg-grades");

        AssertCreatedBy(rows!.Single(r => r.Id == id), email);
    }

    [Fact]
    public async Task ExpenseList_CarriesProvenance()
    {
        var (client, email) = await AuthedAsync();
        var categoryId = await CreatedIdAsync(await client.PostWithKeyAsync(
            "/api/v1/expense-categories", Guid.NewGuid().ToString(), new { name = "Feed" }));
        var id = await CreatedIdAsync(await client.PostWithKeyAsync(
            "/api/v1/expenses", Guid.NewGuid().ToString(), new
            {
                expenseCategoryId = categoryId,
                date = DateOnly.FromDateTime(DateTime.UtcNow.Date),
                description = "Feed delivery",
                amountMinorUnits = 25_00,
            }));

        var list = await client.GetFromJsonAsync<ExpenseListDto>("/api/v1/expenses");

        AssertCreatedBy(list!.Items.Single(r => r.Id == id), email);
    }

    [Fact]
    public async Task SalesOrderList_CarriesProvenance()
    {
        var (client, email) = await AuthedAsync();
        var customerId = await CreatedIdAsync(await client.PostWithKeyAsync(
            "/api/v1/customers", Guid.NewGuid().ToString(),
            new { name = "Mercado Central", phone = "555-0100" }));
        var id = await CreatedIdAsync(await client.PostWithKeyAsync(
            "/api/v1/sales", Guid.NewGuid().ToString(),
            new { customerId, orderDate = DateOnly.FromDateTime(DateTime.UtcNow.Date) }));

        var rows = await client.GetFromJsonAsync<List<ProvenanceRowDto>>("/api/v1/sales");

        AssertCreatedBy(rows!.Single(r => r.Id == id), email);
    }

    private async Task<(HttpClient Client, string Email, Guid AccountId, Guid EntryId)> DraftDailyEntryAsync()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var farmId = Guid.NewGuid();
        var grades = await factory.SeedEggGradesAsync(accountId, farmId, "Large");
        var flockId = await factory.SeedFlockAsync(accountId, farmId);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        // #394: submit requires exact reconciliation, so the draft carries a
        // grade line summing to the total — otherwise every submit here 422s
        // and the tests below pass for the wrong reason.
        var id = await CreatedIdAsync(await client.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(), new
            {
                farmId,
                houseId = Guid.NewGuid(),
                flockId,
                date = DateOnly.FromDateTime(DateTime.UtcNow.Date),
                totalEggs = 100,
                crackedEggs = 0,
                dirtyEggs = 0,
                discardedEggs = 0,
                mortalityCount = 0,
                grades = new[] { new { eggGradeId = grades["Large"], quantity = 100 } },
            }));
        return (client, email, accountId, id);
    }

    private async Task<(HttpClient Client, string Email, Guid AccountId, Guid OrderId)> DraftSalesOrderAsync()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var farmId = Guid.NewGuid();
        var grades = await factory.SeedEggGradesAsync(accountId, farmId, "Large");
        var productId = await factory.SeedProductAsync(accountId, farmId, grades["Large"], "Large Eggs");
        // Confirm allocates stock FIFO, so there has to be stock to draw from —
        // otherwise every confirm below 422s and the tests pass for no reason.
        await factory.SeedEggLotAsync(accountId, grades["Large"], 500);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        var customerId = await CreatedIdAsync(await client.PostWithKeyAsync(
            "/api/v1/customers", Guid.NewGuid().ToString(),
            new { name = "Mercado Central", phone = "555-0100" }));
        var orderId = await CreatedIdAsync(await client.PostWithKeyAsync(
            "/api/v1/sales", Guid.NewGuid().ToString(),
            new { customerId, orderDate = DateOnly.FromDateTime(DateTime.UtcNow.Date) }));
        var addItem = await client.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/items", Guid.NewGuid().ToString(),
            new { productId, quantity = 10, unitPriceMinorUnits = 100 });
        // A silently-failing add-item leaves an empty order that cannot confirm,
        // which would make every test built on this fixture vacuous.
        Assert.Equal(HttpStatusCode.Created, addItem.StatusCode);
        return (client, email, accountId, orderId);
    }

    private async Task<List<string>> ActionsForAsync(Guid accountId, Guid entityId, string entityType) =>
        await factory.WithTenantScopeAsync(accountId, async db =>
            await db.AuditEvents
                .Where(e => e.EntityId == entityId && e.EntityType == entityType)
                .Select(e => e.Action)
                .ToListAsync());

    // Confirming allocates stock FIFO, so it belongs on the trail for the same
    // reason submitting a daily entry does.
    [Fact]
    public async Task SalesOrderConfirm_WritesAnAuditEvent()
    {
        var (client, _, accountId, orderId) = await DraftSalesOrderAsync();

        var response = await client.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/confirm", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Contains("SalesOrder.Confirm", await ActionsForAsync(accountId, orderId, "SalesOrder"));
    }

    [Fact]
    public async Task SalesOrderCancel_WritesAnAuditEvent()
    {
        var (client, _, accountId, orderId) = await DraftSalesOrderAsync();

        var response = await client.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/cancel", Guid.NewGuid().ToString());
        // 204, unlike confirm's 200 — cancel returns no body.
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        Assert.Contains("SalesOrder.Cancel", await ActionsForAsync(accountId, orderId, "SalesOrder"));
    }

    // End to end: one person drafts an order and confirms it. Two events, one
    // act — the row still reports a creator and no change.
    [Fact]
    public async Task SalesOrderList_WhenTheCreatorConfirmsTheirOwnOrder_ReportsNoChange()
    {
        var (client, email, _, orderId) = await DraftSalesOrderAsync();
        // Asserted, not discarded — see the daily-entry twin above.
        var confirm = await client.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/confirm", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);

        var rows = await client.GetFromJsonAsync<List<ProvenanceRowDto>>("/api/v1/sales");

        var row = rows!.Single(r => r.Id == orderId);
        AssertCreatedBy(row, email);
        Assert.NotNull(row.MadeOfficialAtUtc);
    }

    [Fact]
    public async Task DailyEntryList_CarriesProvenance()
    {
        var (client, email, _, id) = await DraftDailyEntryAsync();

        var rows = await client.GetFromJsonAsync<List<ProvenanceRowDto>>("/api/v1/daily-entries");

        AssertCreatedBy(rows!.Single(r => r.Id == id), email);
    }

    // Submitting is a stock-altering transition (it mints the egg lots), so it
    // belongs in the trail on its own merits. It is also the ONLY source for the
    // "who made this official" half of a daily entry's record history — without
    // this event, a manager submitting a worker's draft leaves no trace at all.
    // Re-recording against the same natural key rewrites an existing draft. It
    // moves no stock, but it is the only thing binding a person to the numbers,
    // so it is on the trail — see Provenance_WhenSomeoneElseEditsTheDraft_...
    [Fact]
    public async Task DailyEntryDraftEdit_WritesAnAuditEvent()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var farmId = Guid.NewGuid();
        var houseId = Guid.NewGuid();
        var grades = await factory.SeedEggGradesAsync(accountId, farmId, "Large");
        var flockId = await factory.SeedFlockAsync(accountId, farmId);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        // Same natural key both times, so the second call appends to the first
        // entry rather than creating a second one.
        object Body(int total) => new
        {
            farmId, houseId, flockId,
            date = DateOnly.FromDateTime(DateTime.UtcNow.Date),
            totalEggs = total, crackedEggs = 0, dirtyEggs = 0, discardedEggs = 0,
            mortalityCount = 0,
            grades = new[] { new { eggGradeId = grades["Large"], quantity = total } },
        };

        var id = await CreatedIdAsync(await client.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(), Body(100)));
        var again = await client.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(), Body(500));
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);

        var actions = await ActionsForAsync(accountId, id, "DailyEntry");
        Assert.Contains("DailyEntry.Update", actions);
        // Still exactly one creation: two would give the entry two candidate
        // authors and let the later one win.
        Assert.Single(actions, a => a == "DailyEntry.Create");
    }

    [Fact]
    public async Task DailyEntrySubmit_WritesAnAuditEvent()
    {
        var (client, _, accountId, id) = await DraftDailyEntryAsync();

        var response = await client.PostWithKeyAsync(
            $"/api/v1/daily-entries/{id}/submit", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Contains("DailyEntry.Submit", await ActionsForAsync(accountId, id, "DailyEntry"));
    }

    // The end-to-end shape of #494's answer: one person writes the day's numbers
    // and makes them official, having changed nothing in between. Two audit
    // events, one act — the row still reports a creator and NO change.
    //
    // Load-bearing only once the submit event above exists; before that it would
    // pass for the wrong reason. Mutation-checked by removing the self-submit
    // exclusion from AuditEventRepository, which turns this red.
    [Fact]
    public async Task DailyEntryList_WhenTheCreatorSubmitsTheirOwnDraft_ReportsNoChange()
    {
        var (client, email, _, id) = await DraftDailyEntryAsync();
        // Asserted, not discarded: a submit that started 422ing would leave the
        // trail at ".Create" only, and this test would stay green while pinning
        // nothing (adversarial review of PR #503).
        var submit = await client.PostWithKeyAsync(
            $"/api/v1/daily-entries/{id}/submit", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);

        var rows = await client.GetFromJsonAsync<List<ProvenanceRowDto>>("/api/v1/daily-entries");

        var row = rows!.Single(r => r.Id == id);
        AssertCreatedBy(row, email);
        // The submit is excluded from "last changed by", but WHEN it happened
        // still has to reach the page — it is the instant the eggs entered
        // stock, and the entry stores no SubmittedAt of its own.
        Assert.NotNull(row.MadeOfficialAtUtc);
    }

    // Most callers hand this a clamped page, but the egg-grade list has no
    // pagination at all and grades are never deleted — a farm's catalog grows
    // past any fixed cap eventually. So the batch size is an internal chunking
    // detail, NOT a caller contract: more ids than fit in one round trip must
    // still answer correctly rather than fail the whole list endpoint.
    [Fact]
    public async Task Provenance_AboveTheBatchSize_StillResolvesEveryId()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"u-{Guid.NewGuid():N}@test.local");
        var ids = Enumerable.Range(0, IAuditEventRepository.MaxBatchIds + 25)
            .Select(_ => Guid.NewGuid()).ToArray();
        // Seed an event for every id so a dropped chunk shows up as a missing key.
        await SeedEventsAsync(accountId,
            ids.Select(id => Event(accountId, id, "Flock.Create", "ana@farm.test", 0)).ToArray());

        var result = await WithRepositoryAsync(accountId, repo =>
            repo.GetProvenanceAsync("Flock", ids));

        Assert.Equal(ids.Length, result.Count);
        Assert.All(ids, id => Assert.Equal("ana@farm.test", result[id].CreatedByEmail));
    }
}
