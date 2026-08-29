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
        string? DateFormatOverride, string? TimeFormatOverride, int Version,
        string? LogoContentHash, string Brand, string DefaultStepperUnit,
        string? BannerContentHash, bool ShowFarmWideSaleAllocationNotice);
    private sealed record SettingsDto(
        AccountDto Settings, bool CanChangeCurrency, int LogoMaxUploadBytes,
        string WorkerSaleAllocationPolicy);
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
        string? dateFormatOverride = null, string? timeFormatOverride = null,
        string? brand = null, string? defaultStepperUnit = null,
        string? workerSaleAllocationPolicy = null, int? version = null) => new
        {
            name = name ?? current.Name,
            timeZoneId = timeZoneId ?? current.TimeZoneId,
            locale = locale ?? current.Locale,
            currencyCode = currencyCode ?? current.CurrencyCode,
            unitSystem = unitSystem ?? current.UnitSystem,
            firstDayOfWeek = firstDayOfWeek ?? current.FirstDayOfWeek,
            dateFormatOverride = dateFormatOverride ?? current.DateFormatOverride,
            timeFormatOverride = timeFormatOverride ?? current.TimeFormatOverride,
            brand = brand ?? current.Brand,
            defaultStepperUnit = defaultStepperUnit ?? current.DefaultStepperUnit,
            workerSaleAllocationPolicy = workerSaleAllocationPolicy ?? "AssignedFlocksOnly",
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
    public async Task Settings_CarryTheConfiguredLogoUploadCap()
    {
        // The SPA reads this rather than hardcoding a limit (#123). The test
        // host configures the cap (CluckworkWebApplicationFactory), so the
        // payload must report exactly it.
        var (client, _, _) = await AdminAsync();

        var settings = await client.GetFromJsonAsync<SettingsDto>(SettingsPath);

        Assert.Equal(CluckworkWebApplicationFactory.LogoUploadCap, settings!.LogoMaxUploadBytes);
    }

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
    // There is no allowlist of "supported" currencies — validation is ISO 4217
    // shape only, the symbol comes from ICU and the minor unit from the
    // standard. These two are pinned end-to-end because they were asked for by
    // name, and because they are the two shapes worth having a real example of:
    // one with its own symbol, one that shares "$" with the dollar.
    [Theory]
    [InlineData("PHP", "₱", 2)]
    [InlineData("MXN", "$", 2)]
    public async Task AFarmCanOperateIn(string currencyCode, string symbol, int minorUnit)
    {
        var (client, _, _) = await AdminAsync();
        var current = await GetAccountAsync(client);

        var saved = await PutSettingsAsync(client, Body(current, currencyCode: currencyCode));
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);

        var after = await GetAccountAsync(client);
        Assert.Equal(currencyCode, after.CurrencyCode);
        Assert.Equal(symbol, after.CurrencySymbol);
        Assert.Equal(minorUnit, after.CurrencyMinorUnit);
    }

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
    public async Task CurrencyChange_AfterAStockLotExists_IsRefused()
    {
        // The item-with-a-default-cost case above leaves the InventoryLots and
        // FeedUsages probes untested — drop either and it stays green (codex
        // review of #159). A purchase writes a lot, and every lot stores a cost.
        var (client, _, _) = await AdminAsync();

        var item = await client.PostWithKeyAsync("/api/v1/inventory/items", Guid.NewGuid().ToString(),
            new { name = $"Feed {Guid.NewGuid():N}"[..14], category = "Feed", unit = "kg", defaultUnitCostMinorUnits = (long?)null });
        var itemId = (await item.Content.ReadFromJsonAsync<IdDto>())!.Id;

        var purchase = await client.PostWithKeyAsync(
            $"/api/v1/inventory/items/{itemId}/purchases", Guid.NewGuid().ToString(),
            new { receivedDate = DateOnly.FromDateTime(DateTime.UtcNow.Date), quantity = 10m, unitCostMinorUnits = 100L, lotNumber = (string?)null, expiryDate = (DateOnly?)null, note = (string?)null });
        Assert.Equal(HttpStatusCode.Created, purchase.StatusCode);

        await AssertCurrencyLockedAsync(client);
    }

    [Fact]
    public async Task CurrencyChange_WithOnlyACostlessInventoryItem_IsStillAllowed()
    {
        // The mirror of the case above, and the one that proves the owned-Money
        // null check translates to real SQL rather than matching everything: an
        // item carrying no default cost has recorded no amount, so it must not
        // lock the farm out of a currency it has not started trading in.
        var (client, _, _) = await AdminAsync();

        var item = await client.PostWithKeyAsync("/api/v1/inventory/items", Guid.NewGuid().ToString(),
            new { name = $"Feed {Guid.NewGuid():N}"[..14], category = "Feed", unit = "kg", defaultUnitCostMinorUnits = (long?)null });
        Assert.Equal(HttpStatusCode.Created, item.StatusCode);

        var settings = await client.GetFromJsonAsync<SettingsDto>(SettingsPath);
        Assert.True(settings!.CanChangeCurrency);

        var saved = await PutSettingsAsync(client, Body(settings.Settings, currencyCode: "JPY"));
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
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

    // --- default stepper unit (#444) ---------------------------------------

    [Fact]
    public async Task DefaultStepperUnit_DefaultsToIndividual()
    {
        var (client, _, _) = await AdminAsync();

        var account = await GetAccountAsync(client);

        Assert.Equal("Individual", account.DefaultStepperUnit);
    }

    [Fact]
    public async Task DefaultStepperUnit_RoundTripsThroughTheSettingsEndpoint()
    {
        var (client, _, _) = await AdminAsync();
        var before = (await client.GetFromJsonAsync<SettingsDto>(SettingsPath))!.Settings;

        var response = await PutSettingsAsync(client, Body(before, defaultStepperUnit: "Tray"));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var after = (await client.GetFromJsonAsync<SettingsDto>(SettingsPath))!.Settings;
        Assert.Equal("Tray", after.DefaultStepperUnit);

        // And through the role-agnostic read DailyEntryPage actually uses.
        var account = await client.GetFromJsonAsync<AccountDto>("/api/v1/account");
        Assert.Equal("Tray", account!.DefaultStepperUnit);
    }

    [Fact]
    public async Task UnknownStepperUnit_Is400()
    {
        var (client, _, _) = await AdminAsync();
        var before = await GetAccountAsync(client);

        var response = await PutSettingsAsync(client, Body(before, defaultStepperUnit: "Bushel"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Individual", (await GetAccountAsync(client)).DefaultStepperUnit);
    }

    // Same reasoning as MeEndpointsTests.Unit_with_no_active_conversion_is_a_422:
    // "Other" is the one EggUnit member #283's base reference data deliberately
    // leaves unseeded.
    [Fact]
    public async Task StepperUnitWithNoActiveConversion_Is422()
    {
        var (client, _, _) = await AdminAsync();
        var before = await GetAccountAsync(client);

        var response = await PutSettingsAsync(client, Body(before, defaultStepperUnit: "Other"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("FarmSettings.NoUnitConversion",
            (await response.Content.ReadFromJsonAsync<ProblemDto>())!.Title);
        Assert.Equal("Individual", (await GetAccountAsync(client)).DefaultStepperUnit);
    }

    // --- accent palette (#149) ---------------------------------------------

    [Fact]
    public async Task Brand_DefaultsToAubergine_AndRidesTheRoleAgnosticAccountRead()
    {
        var (client, _, _) = await AdminAsync();

        // /account, not /account/settings: the palette is farm-wide, so every
        // role needs it, and the settings endpoint is admin-only.
        var account = await client.GetFromJsonAsync<AccountDto>("/api/v1/account");

        Assert.NotNull(account);
        Assert.Equal("aubergine", account.Brand);
    }

    [Fact]
    public async Task Brand_RoundTripsThroughTheSettingsEndpoint()
    {
        var (client, _, _) = await AdminAsync();
        var before = (await client.GetFromJsonAsync<SettingsDto>(SettingsPath))!.Settings;

        var response = await PutSettingsAsync(client, Body(before, brand: "forest"));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var after = (await client.GetFromJsonAsync<SettingsDto>(SettingsPath))!.Settings;
        Assert.Equal("forest", after.Brand);
        Assert.Equal(before.Version + 1, after.Version);

        // And through the role-agnostic read the SPA shell actually uses.
        var account = await client.GetFromJsonAsync<AccountDto>("/api/v1/account");
        Assert.Equal("forest", account!.Brand);
    }

    [Fact]
    public async Task UnknownBrand_Is422_WithAStableCode()
    {
        // The contract #149 asks for. It works because the aggregate rejects the
        // brand and MapFailure's fallback arm turns any unrecognised domain code
        // into a 422 whose title IS the code — no bespoke plumbing.
        var (client, _, _) = await AdminAsync();
        var before = (await client.GetFromJsonAsync<SettingsDto>(SettingsPath))!.Settings;

        var response = await PutSettingsAsync(client, Body(before, brand: "chartreuse"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("Account.UnknownBrand",
            (await response.Content.ReadFromJsonAsync<ProblemDto>())!.Title);

        // And nothing was written.
        var after = (await client.GetFromJsonAsync<SettingsDto>(SettingsPath))!.Settings;
        Assert.Equal("aubergine", after.Brand);
        Assert.Equal(before.Version, after.Version);
    }

    [Fact]
    public async Task Brand_IsStoredLowercaseWhateverTheCasingSent()
    {
        var (client, _, _) = await AdminAsync();
        var before = (await client.GetFromJsonAsync<SettingsDto>(SettingsPath))!.Settings;

        var response = await PutSettingsAsync(client, Body(before, brand: "  Terracotta  "));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Anything but lowercase would never match the exact-match CSS selector.
        var after = (await client.GetFromJsonAsync<SettingsDto>(SettingsPath))!.Settings;
        Assert.Equal("terracotta", after.Brand);
    }

    [Fact]
    public async Task BrandChange_IsRecordedInTheAuditTrailOnBothSides()
    {
        var (client, accountId, _) = await AdminAsync();
        var before = (await client.GetFromJsonAsync<SettingsDto>(SettingsPath))!.Settings;

        Assert.Equal(HttpStatusCode.NoContent,
            (await PutSettingsAsync(client, Body(before, brand: "slate"))).StatusCode);

        // WithTenantScopeAsync, not a bare CreateScope: AuditEvent carries a
        // tenant query filter, so an unresolved TenantContext makes the row
        // invisible and FirstAsync throws rather than failing the assertion.
        var details = await factory.WithTenantScopeAsync(accountId, async db =>
            await db.AuditEvents
                .Where(e => e.Action == "Account.UpdateSettings")
                .OrderByDescending(e => e.OccurredAtUtc)
                .Select(e => e.DetailsJson)
                .FirstAsync());

        // The snapshot is an explicit field list, so a new aggregate property is
        // NOT picked up automatically — assert the values, not just the event.
        Assert.NotNull(details);
        Assert.Contains("\"aubergine\"", details);
        Assert.Contains("\"slate\"", details);
    }

    [Fact]
    public async Task ParallelBrandSaves_SameBaseVersion_ExactlyOneWins()
    {
        // Races two DIFFERENT brands rather than two arbitrary saves: this only
        // passes if Brand is inside the atomic whole-settings replacement guarded
        // by the Version concurrency token, not merely that the token works.
        var (client, _, _) = await AdminAsync();
        var before = (await client.GetFromJsonAsync<SettingsDto>(SettingsPath))!.Settings;

        var first = PutSettingsAsync(client, Body(before, brand: "forest"));
        var second = PutSettingsAsync(client, Body(before, brand: "terracotta"));
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.NoContent));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        var after = (await client.GetFromJsonAsync<SettingsDto>(SettingsPath))!.Settings;
        // Exactly one of the two, never a blend and never the pre-race default.
        Assert.Contains(after.Brand, new[] { "forest", "terracotta" });
        Assert.Equal(before.Version + 1, after.Version);
    }

    // --- worker sale-allocation policy (#612) -------------------------------

    [Fact]
    public async Task WorkerSaleAllocationPolicy_DefaultsToAssignedFlocksOnly()
    {
        var (client, _, _) = await AdminAsync();

        var settings = await client.GetFromJsonAsync<SettingsDto>(SettingsPath);

        Assert.Equal("AssignedFlocksOnly", settings!.WorkerSaleAllocationPolicy);
    }

    [Fact]
    public async Task WorkerSaleAllocationPolicy_RoundTripsThroughTheSettingsEndpoint()
    {
        var (client, _, _) = await AdminAsync();
        var before = await client.GetFromJsonAsync<SettingsDto>(SettingsPath);

        var response = await PutSettingsAsync(client,
            Body(before!.Settings, workerSaleAllocationPolicy: "AllFarmFlocks"));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var after = await client.GetFromJsonAsync<SettingsDto>(SettingsPath);
        Assert.Equal("AllFarmFlocks", after!.WorkerSaleAllocationPolicy);
        Assert.Equal(before.Settings.Version + 1, after.Settings.Version);
    }

    [Fact]
    public async Task UnknownWorkerSaleAllocationPolicy_Is400()
    {
        var (client, _, _) = await AdminAsync();
        var before = await client.GetFromJsonAsync<SettingsDto>(SettingsPath);

        var response = await PutSettingsAsync(client,
            Body(before!.Settings, workerSaleAllocationPolicy: "SomethingElse"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("AssignedFlocksOnly",
            (await client.GetFromJsonAsync<SettingsDto>(SettingsPath))!.WorkerSaleAllocationPolicy);
    }

    [Fact]
    public async Task WorkerSaleAllocationPolicyChange_IsAudited_WithBothSidesOfTheBlock()
    {
        var (client, accountId, _) = await AdminAsync();
        var before = await client.GetFromJsonAsync<SettingsDto>(SettingsPath);

        Assert.Equal(HttpStatusCode.NoContent, (await PutSettingsAsync(
            client, Body(before!.Settings, workerSaleAllocationPolicy: "AllFarmFlocks"))).StatusCode);

        var details = await factory.WithTenantScopeAsync(accountId, async db =>
            await db.AuditEvents
                .Where(e => e.Action == "Account.UpdateSettings")
                .OrderByDescending(e => e.OccurredAtUtc)
                .Select(e => e.DetailsJson)
                .FirstAsync());

        Assert.Contains("\"AssignedFlocksOnly\"", details);
        Assert.Contains("\"AllFarmFlocks\"", details);
    }

    [Fact]
    public async Task ParallelPolicySaves_SameBaseVersion_ExactlyOneWins()
    {
        // Same shape as ParallelBrandSaves_SameBaseVersion_ExactlyOneWins: a
        // policy change now takes the Account FOR UPDATE lock like a currency
        // change, so this only passes if that lock genuinely serializes the
        // two writers rather than the in-memory version check alone.
        var (client, _, _) = await AdminAsync();
        var before = await client.GetFromJsonAsync<SettingsDto>(SettingsPath);

        var first = PutSettingsAsync(client,
            Body(before!.Settings, workerSaleAllocationPolicy: "AllFarmFlocks"));
        var second = PutSettingsAsync(client,
            Body(before.Settings, name: "Second writer", workerSaleAllocationPolicy: "AllFarmFlocks"));
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.NoContent));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        var after = await client.GetFromJsonAsync<SettingsDto>(SettingsPath);
        Assert.Equal(before.Settings.Version + 1, after!.Settings.Version);
    }

    [Fact]
    public async Task ShowFarmWideSaleAllocationNotice_IsAlwaysFalse_ForAnAdmin()
    {
        var (client, _, _) = await AdminAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await PutSettingsAsync(client,
            Body((await client.GetFromJsonAsync<SettingsDto>(SettingsPath))!.Settings,
                workerSaleAllocationPolicy: "AllFarmFlocks"))).StatusCode);

        Assert.False((await GetAccountAsync(client)).ShowFarmWideSaleAllocationNotice);
    }

    [Fact]
    public async Task ShowFarmWideSaleAllocationNotice_IsFalse_ForAnUnrestrictedWorker_EvenUnderAllFarmFlocks()
    {
        var (admin, accountId, _) = await AdminAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await PutSettingsAsync(
            admin, Body(await client_SettingsBody(admin), workerSaleAllocationPolicy: "AllFarmFlocks"))).StatusCode);

        var workerEmail = $"w-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, workerEmail, asAdmin: false);
        var worker = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(workerEmail));

        // No UserRoleAssignment rows at all (grandfathered #73) => Unrestricted.
        Assert.False((await GetAccountAsync(worker)).ShowFarmWideSaleAllocationNotice);
    }

    [Fact]
    public async Task ShowFarmWideSaleAllocationNotice_IsFalse_ForARestrictedWorker_UnderAssignedFlocksOnly()
    {
        var (admin, accountId, _) = await AdminAsync();

        var workerEmail = $"w-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, workerEmail, asAdmin: false);
        var worker = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(workerEmail));
        await SeedFlockAssignmentAsync(accountId, workerEmail);

        // Default policy — the farm never opted into farm-wide allocation, so
        // there is nothing for the notice to explain.
        Assert.False((await GetAccountAsync(worker)).ShowFarmWideSaleAllocationNotice);
    }

    [Fact]
    public async Task ShowFarmWideSaleAllocationNotice_IsTrue_ForARestrictedWorker_UnderAllFarmFlocks()
    {
        var (admin, accountId, _) = await AdminAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await PutSettingsAsync(
            admin, Body(await client_SettingsBody(admin), workerSaleAllocationPolicy: "AllFarmFlocks"))).StatusCode);

        var workerEmail = $"w-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, workerEmail, asAdmin: false);
        var worker = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(workerEmail));
        await SeedFlockAssignmentAsync(accountId, workerEmail);

        Assert.True((await GetAccountAsync(worker)).ShowFarmWideSaleAllocationNotice);
    }

    private async Task<AccountDto> client_SettingsBody(HttpClient client) =>
        (await client.GetFromJsonAsync<SettingsDto>(SettingsPath))!.Settings;

    // Direct EF insert rather than the full AssignFlock HTTP flow (step-up
    // grants, target-role checks) — this slice only needs a live restricted
    // scope for FlockScope to resolve, same fixture shape FlockScope's own
    // middleware tests use.
    private async Task SeedFlockAssignmentAsync(Guid accountId, string email)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider
            .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Cluckwork.Infrastructure.Identity.ApplicationUser>>();
        var user = await users.FindByEmailAsync(email)
            ?? throw new InvalidOperationException($"No user {email}");

        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            db.UserRoleAssignments.Add(Cluckwork.Domain.Accounts.UserRoleAssignment.Create(
                Guid.NewGuid(), accountId, user.Id, farmId: null, houseId: null, flockId: Guid.NewGuid()));
            await db.SaveChangesAsync();
        });
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
