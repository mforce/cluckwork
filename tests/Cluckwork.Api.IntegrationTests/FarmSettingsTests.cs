namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// #123 slice 1 — farm settings over the wire: the read every role needs for
// §4.5 formatting, the admin-only write, §4.6's currency lock, and the two
// guards that keep the settings themselves trustworthy (version token,
// timezone validation).
[Collection(IntegrationCollection.Name)]
public sealed class FarmSettingsTests(CluckworkWebApplicationFactory factory)
{
    private sealed record AccountDto(
        Guid Id, string Name, string CurrencyCode, int CurrencyMinorUnit, string CurrencySymbol,
        string TimeZoneId, string Locale, string UnitSystem, string? FirstDayOfWeek,
        string? DateFormatOverride, string? TimeFormatOverride, int Version);
    private sealed record SettingsDto(AccountDto Settings, bool CanChangeCurrency);
    private sealed record ProblemDto(string? Title);
    private sealed record IdDto(Guid Id);

    private const string SettingsPath = "/api/v1/account/settings";
    private static readonly Guid FarmId = Cluckwork.Domain.Accounts.SeedDefaults.FarmId;

    private async Task<(HttpClient Client, Guid AccountId, string Email)> AdminAsync()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        return (factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email)), accountId, email);
    }

    private static Task<HttpResponseMessage> PutSettingsAsync(HttpClient client, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, SettingsPath)
        { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return client.SendAsync(request);
    }

    // The full block, so a partial body can never be mistaken for a pass.
    private static object Body(
        AccountDto current,
        string? name = null, string? timeZoneId = null, string? locale = null,
        string? currencyCode = null, string? unitSystem = null, string? firstDayOfWeek = null,
        string? dateFormatOverride = null, string? timeFormatOverride = null, int? version = null) => new
        {
            name = name ?? current.Name,
            timeZoneId = timeZoneId ?? current.TimeZoneId,
            locale = locale ?? current.Locale,
            currencyCode = currencyCode ?? current.CurrencyCode,
            unitSystem = unitSystem ?? current.UnitSystem,
            firstDayOfWeek = firstDayOfWeek ?? current.FirstDayOfWeek,
            dateFormatOverride = dateFormatOverride ?? current.DateFormatOverride,
            timeFormatOverride = timeFormatOverride ?? current.TimeFormatOverride,
            version = version ?? current.Version
        };

    private static Task<AccountDto> GetAccountAsync(HttpClient client) =>
        client.GetFromJsonAsync<AccountDto>("/api/v1/account")!;

    // --- read -------------------------------------------------------------

    [Fact]
    public async Task GetAccount_CarriesTheLocalizationBlock()
    {
        var (client, _, _) = await AdminAsync();

        var account = await GetAccountAsync(client);

        Assert.Equal("UTC", account.TimeZoneId);
        Assert.Equal("en-US", account.Locale);
        Assert.Equal("USD", account.CurrencyCode);
        Assert.Equal(2, account.CurrencyMinorUnit);
        Assert.Equal("$", account.CurrencySymbol);
        Assert.Equal("Metric", account.UnitSystem);
        Assert.Null(account.FirstDayOfWeek);
    }

    [Fact]
    public async Task GetAccount_IsOpenToEveryRole_BecauseFormattingIs()
    {
        // §4.5's display rule applies to every screen, so a read-only viewer
        // needs locale/currency/timezone as much as an owner does.
        var (_, accountId, _) = await AdminAsync();
        var viewerEmail = $"v-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, viewerEmail, Cluckwork.Domain.Accounts.Roles.ReadOnly);
        var viewer = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(viewerEmail));

        var account = await GetAccountAsync(viewer);

        // Asserting on the PAYLOAD, not just a 200: /api/v1/account existed
        // before #123 inside the same open group, so a status-only check would
        // pass with this whole slice reverted (adversarial review of #159).
        Assert.Equal("en-US", account.Locale);
        Assert.Equal("UTC", account.TimeZoneId);
        Assert.Equal("$", account.CurrencySymbol);
        Assert.Equal("Metric", account.UnitSystem);
    }

    // --- write ------------------------------------------------------------

    [Fact]
    public async Task Admin_ReplacesTheBlock_AndTheReadReflectsIt()
    {
        var (client, _, _) = await AdminAsync();
        var before = await GetAccountAsync(client);

        var saved = await PutSettingsAsync(client, Body(before,
            name: "Sunrise Layers",
            timeZoneId: "America/Los_Angeles",
            locale: "es-MX",
            unitSystem: "Imperial",
            firstDayOfWeek: "Monday",
            dateFormatOverride: "dd/MM/yyyy",
            timeFormatOverride: "HH:mm"));
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);

        var after = await GetAccountAsync(client);
        Assert.Equal("Sunrise Layers", after.Name);
        Assert.Equal("America/Los_Angeles", after.TimeZoneId);
        Assert.Equal("es-MX", after.Locale);
        Assert.Equal("Imperial", after.UnitSystem);
        Assert.Equal("Monday", after.FirstDayOfWeek);
        Assert.Equal("dd/MM/yyyy", after.DateFormatOverride);
        Assert.Equal("HH:mm", after.TimeFormatOverride);
        Assert.Equal(before.Version + 1, after.Version);
    }

    [Fact]
    public async Task StaleVersion_Is409()
    {
        var (client, _, _) = await AdminAsync();
        var stale = await GetAccountAsync(client);

        var first = await PutSettingsAsync(client, Body(stale, name: "First writer wins"));
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        // Second writer still holds the pre-save version.
        var second = await PutSettingsAsync(client, Body(stale, name: "Second writer"));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("Account.VersionMismatch",
            (await second.Content.ReadFromJsonAsync<ProblemDto>())!.Title);

        Assert.Equal("First writer wins", (await GetAccountAsync(client)).Name);
    }

    [Theory]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("PST")]
    public async Task UnusableTimeZone_Is400_SoTheClockNeverHasToGuess(string timeZoneId)
    {
        // #35's FarmClock refuses to fall back to UTC when the stored zone is
        // unusable. This is the gate that keeps that path unreachable.
        var (client, _, _) = await AdminAsync();
        var current = await GetAccountAsync(client);

        var response = await PutSettingsAsync(client, Body(current, timeZoneId: timeZoneId));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("UTC", (await GetAccountAsync(client)).TimeZoneId);
    }

    [Fact]
    public async Task UnusableLocale_Is400()
    {
        var (client, _, _) = await AdminAsync();
        var current = await GetAccountAsync(client);

        var response = await PutSettingsAsync(client, Body(current, locale: "zz-ZZ"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- admin gate -------------------------------------------------------

    [Fact]
    public async Task SettingsScreenAndWrite_AreAdminOnly()
    {
        var (admin, accountId, _) = await AdminAsync();
        var current = await GetAccountAsync(admin);

        var workerEmail = $"w-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, workerEmail, asAdmin: false);
        var worker = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(workerEmail));

        Assert.Equal(HttpStatusCode.Forbidden, (await worker.GetAsync(SettingsPath)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await PutSettingsAsync(worker, Body(current, name: "Worker rename"))).StatusCode);
        Assert.Equal(current.Name, (await GetAccountAsync(admin)).Name);
    }

    // Every role that is NOT the gate, not only the worker: AdminOnly admits
    // Owner and Manager since #103, so Sales and ReadOnly need their own cases
    // and Manager needs proof it is genuinely admitted, not just tolerated by a
    // test that never tries (adversarial review of #159).
    [Theory]
    [InlineData(Cluckwork.Domain.Accounts.Roles.Sales)]
    [InlineData(Cluckwork.Domain.Accounts.Roles.ReadOnly)]
    public async Task SettingsWrite_IsRefusedTo(string role)
    {
        var (admin, accountId, _) = await AdminAsync();
        var current = await GetAccountAsync(admin);

        var email = $"r-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, email, role);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(SettingsPath)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await PutSettingsAsync(client, Body(current, name: $"{role} rename"))).StatusCode);
    }

    [Fact]
    public async Task Manager_CanEditSettings()
    {
        // The whole point of AdminOnly post-#103: it is Owner + Manager, not
        // Owner alone. Farm configuration is a Manager capability (§5.1).
        var (admin, accountId, _) = await AdminAsync();
        var current = await GetAccountAsync(admin);

        var email = $"m-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, email, Cluckwork.Domain.Accounts.Roles.Manager);
        var manager = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync(SettingsPath)).StatusCode);
        var saved = await PutSettingsAsync(manager, Body(current, name: "Manager rename"));

        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        Assert.Equal("Manager rename", (await GetAccountAsync(admin)).Name);
    }

    [Fact]
    public async Task OneFarmsSettingsWrite_CannotReachAnother()
    {
        var (_, accountA, _) = await AdminAsync();
        var (clientB, _, _) = await AdminAsync();
        var currentB = await GetAccountAsync(clientB);

        var saved = await PutSettingsAsync(clientB, Body(currentB, name: "B renamed itself"));
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);

        // A's row is untouched — the settings write names no account, it takes
        // the one the tenant filter hands it.
        var nameOfA = await factory.WithTenantScopeAsync(accountA, async db =>
            (await db.Accounts.AsNoTracking().SingleAsync()).Name);
        Assert.Equal("Test Farm Co", nameOfA);
    }

    // --- §4.6 currency lock -----------------------------------------------

    [Fact]
    public async Task CurrencyChange_OnAFarmWithNoFinancialRows_RederivesSymbolAndMinorUnit()
    {
        var (client, _, _) = await AdminAsync();

        var settings = await client.GetFromJsonAsync<SettingsDto>(SettingsPath);
        Assert.True(settings!.CanChangeCurrency);

        var saved = await PutSettingsAsync(client, Body(settings.Settings, currencyCode: "JPY"));
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);

        var after = await GetAccountAsync(client);
        Assert.Equal("JPY", after.CurrencyCode);
        Assert.Equal(0, after.CurrencyMinorUnit);   // §4.6 derivation, not the default 2
        Assert.Equal("¥", after.CurrencySymbol);
    }

    [Fact]
    public async Task CurrencyChange_AfterAnExpenseExists_Is422_AndTheScreenIsWarnedFirst()
    {
        var (client, _, _) = await AdminAsync();
        await SeedOneExpenseAsync(client);

        // The screen learns the field is locked before the user tries.
        var settings = await client.GetFromJsonAsync<SettingsDto>(SettingsPath);
        Assert.False(settings!.CanChangeCurrency);

        var response = await PutSettingsAsync(client,
            Body(settings.Settings, name: "Renamed too", currencyCode: "JPY"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("Account.CurrencyLocked",
            (await response.Content.ReadFromJsonAsync<ProblemDto>())!.Title);

        // The refusal covers the whole save — the rename did not sneak through.
        var after = await GetAccountAsync(client);
        Assert.Equal("USD", after.CurrencyCode);
        Assert.NotEqual("Renamed too", after.Name);
    }

    // Each of the four currency-bound tables gets its own case: with only the
    // expense case, dropping any of the other three probes would leave the
    // suite green (codex review of #159).
    [Fact]
    public async Task CurrencyChange_AfterASalesOrderExists_IsRefused()
    {
        var (client, accountId, _) = await AdminAsync();
        var grades = await factory.SeedEggGradesAsync(accountId, FarmId, "A");
        await factory.SeedSalesOrderAsync(accountId, grades["A"], quantity: 10);

        await AssertCurrencyLockedAsync(client);
    }

    [Fact]
    public async Task CurrencyChange_AfterAPricedProductExists_IsRefused()
    {
        // §4.6 names three tables, then says future financial tables follow the
        // same rule. A product's default price is a raw minor-unit integer in
        // the currency it snapshotted, and an order line that takes that
        // default stamps it with the ORDER's currency — so a $12.34 default
        // would sell as ¥1,234 after a change to JPY.
        var (client, accountId, _) = await AdminAsync();
        var grades = await factory.SeedEggGradesAsync(accountId, FarmId, "A");
        await factory.SeedProductAsync(
            accountId, FarmId, grades["A"], "Priced dozen", defaultPriceMinorUnits: 12_34);

        await AssertCurrencyLockedAsync(client);
    }

    [Fact]
    public async Task CurrencyChange_WithOnlyAnUnpricedProduct_IsStillAllowed()
    {
        // The mirror: nothing reads an unpriced product's currency as an
        // amount, so it must not lock a farm out of a currency it has not
        // started trading in.
        var (client, accountId, _) = await AdminAsync();
        var grades = await factory.SeedEggGradesAsync(accountId, FarmId, "A");
        await factory.SeedProductAsync(
            accountId, FarmId, grades["A"], "Unpriced dozen", defaultPriceMinorUnits: null);

        var settings = await client.GetFromJsonAsync<SettingsDto>(SettingsPath);
        Assert.True(settings!.CanChangeCurrency);

        var saved = await PutSettingsAsync(client, Body(settings.Settings, currencyCode: "JPY"));
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
    }

    private async Task AssertCurrencyLockedAsync(HttpClient client)
    {
        var settings = await client.GetFromJsonAsync<SettingsDto>(SettingsPath);
        Assert.False(settings!.CanChangeCurrency);

        var response = await PutSettingsAsync(client, Body(settings.Settings, currencyCode: "JPY"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("Account.CurrencyLocked",
            (await response.Content.ReadFromJsonAsync<ProblemDto>())!.Title);
        Assert.Equal("USD", (await GetAccountAsync(client)).CurrencyCode);
    }

    [Fact]
    public async Task CurrencyChange_AfterFeedMoneyExists_IsRefused()
    {
        // Not on §4.6's list of three, but a purchase falls back to the item's
        // default cost, which still carries the OLD currency — and feed-usage
        // costing sums lot costs without comparing their currencies
        // (adversarial review of #159).
        var (client, _, _) = await AdminAsync();

        var item = await client.PostWithKeyAsync("/api/v1/inventory/items", Guid.NewGuid().ToString(),
            new { name = $"Feed {Guid.NewGuid():N}"[..14], category = "Feed", unit = "kg", defaultUnitCostMinorUnits = 100L });
        Assert.Equal(HttpStatusCode.Created, item.StatusCode);

        await AssertCurrencyLockedAsync(client);
    }

    [Fact]
    public async Task RejectedSave_LeavesNoAuditRow()
    {
        // The audit write rides the same unit of work; a refused save must not
        // leave a record of a change that never happened.
        var (client, accountId, _) = await AdminAsync();
        var grades = await factory.SeedEggGradesAsync(accountId, FarmId, "A");
        await factory.SeedSalesOrderAsync(accountId, grades["A"], quantity: 10);
        var current = await GetAccountAsync(client);

        var refused = await PutSettingsAsync(client, Body(current, currencyCode: "JPY"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);

        var events = await factory.WithTenantScopeAsync(accountId, async db =>
            await db.AuditEvents.AsNoTracking()
                .CountAsync(e => e.Action == "Account.UpdateSettings"));
        Assert.Equal(0, events);
    }

    [Fact]
    public async Task ParallelSaves_SameBaseVersion_ExactlyOneWins()
    {
        // The sequential StaleVersion test above passes on the handler's own
        // in-memory check alone. This one only passes because Version is a real
        // database concurrency token: both requests read version 0 before
        // either commits, so nothing in application code can separate them
        // (codex review of #159).
        var (client, _, _) = await AdminAsync();
        var before = await GetAccountAsync(client);

        var responses = await Task.WhenAll(
            PutSettingsAsync(client, Body(before, name: "Writer A", locale: "es-MX")),
            PutSettingsAsync(client, Body(before, name: "Writer B", locale: "ja-JP")));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.NoContent));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        var after = await GetAccountAsync(client);
        Assert.Equal(before.Version + 1, after.Version);
        // Whole-payload consistency — the winner's name AND locale, not a blend.
        Assert.True(
            (after.Name == "Writer A" && after.Locale == "es-MX")
            || (after.Name == "Writer B" && after.Locale == "ja-JP"),
            $"blended write: {after.Name} / {after.Locale}");
    }

    [Fact]
    public async Task NonCurrencyEdit_StillWorks_OnceFinancialRowsExist()
    {
        var (client, _, _) = await AdminAsync();
        await SeedOneExpenseAsync(client);
        var current = await GetAccountAsync(client);

        var saved = await PutSettingsAsync(client, Body(current, name: "Renamed", locale: "ja-JP"));

        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        var after = await GetAccountAsync(client);
        Assert.Equal("Renamed", after.Name);
        Assert.Equal("ja-JP", after.Locale);
    }

    // --- audit ------------------------------------------------------------

    [Fact]
    public async Task SettingsChange_IsAudited_WithBothSidesOfTheBlock()
    {
        var (client, accountId, email) = await AdminAsync();
        var current = await GetAccountAsync(client);

        var saved = await PutSettingsAsync(client, Body(current, locale: "ja-JP"));
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);

        var events = await factory.WithTenantScopeAsync(accountId, async db =>
            await db.AuditEvents.AsNoTracking()
                .Where(e => e.Action == "Account.UpdateSettings")
                .ToListAsync());

        var recorded = Assert.Single(events);
        Assert.Equal("Account", recorded.EntityType);
        Assert.Equal(accountId, recorded.EntityId);
        Assert.Equal(email, recorded.ActorEmail);
        Assert.Contains("en-US", recorded.DetailsJson);   // before
        Assert.Contains("ja-JP", recorded.DetailsJson);   // after
    }

    // --- the point of the whole slice -------------------------------------

    [Fact]
    public async Task ChangingTheTimezone_MovesTheFarmsOperationalToday()
    {
        // Frozen an hour past midnight UTC on July 16. A UTC farm is on the
        // 16th; a Los Angeles farm is still on the 15th. Same instant, same
        // request — only the setting differs, and the date rule follows it.
        var email = $"u-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);
        var token = await factory.LoginForAccessTokenAsync(email);
        var frozen = factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddScoped<IClock, FrozenClock>()));
        var client = frozen.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var utcJuly16 = new DateOnly(2026, 7, 16);
        Assert.Equal(HttpStatusCode.Created, (await CreateFlockAsync(client, utcJuly16)).StatusCode);

        var current = await GetAccountAsync(client);
        var saved = await PutSettingsAsync(client, Body(current, timeZoneId: "America/Los_Angeles"));
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);

        // Same date, same instant — now the farm's tomorrow.
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateFlockAsync(client, utcJuly16)).StatusCode);
        Assert.Equal(HttpStatusCode.Created,
            (await CreateFlockAsync(client, utcJuly16.AddDays(-1))).StatusCode);
    }

    private sealed class FrozenClock : IClock
    {
        private static readonly DateTime Instant = new(2026, 7, 16, 1, 0, 0, DateTimeKind.Utc);
        public DateTime UtcNow => Instant;
        public DateOnly TodayUtc => DateOnly.FromDateTime(Instant);
        public DateOnly TodayInZone(string timeZoneId) =>
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
                Instant, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId)));
    }

    private static Task<HttpResponseMessage> CreateFlockAsync(HttpClient client, DateOnly placedOn) =>
        client.PostWithKeyAsync("/api/v1/flocks", Guid.NewGuid().ToString(),
            new { name = $"Barn {Guid.NewGuid():N}"[..12], breed = "ISA Brown", placementDate = placedOn, initialCount = 200 });

    private static async Task SeedOneExpenseAsync(HttpClient client)
    {
        var category = await client.PostWithKeyAsync(
            "/api/v1/expense-categories", Guid.NewGuid().ToString(), new { name = $"Feed {Guid.NewGuid():N}"[..12] });
        Assert.Equal(HttpStatusCode.Created, category.StatusCode);
        var categoryId = (await category.Content.ReadFromJsonAsync<IdDto>())!.Id;

        var expense = await client.PostWithKeyAsync("/api/v1/expenses", Guid.NewGuid().ToString(), new
        {
            expenseCategoryId = categoryId,
            date = DateOnly.FromDateTime(DateTime.UtcNow.Date),
            description = "Feed delivery",
            amountMinorUnits = 12_00L,
            flockId = (Guid?)null,
            note = (string?)null
        });
        Assert.Equal(HttpStatusCode.Created, expense.StatusCode);
    }
}
