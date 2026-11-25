using System.Net;
using System.Text.Json;
using Cluckwork.Api.Hosting;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Api.Modules.Access.Auth;
using Cluckwork.Domain.Modules.Farm.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Serilog.Core;
using Serilog.Events;

namespace Cluckwork.Api.IntegrationTests;

// #1284 (record 1282 decision 5): with Passkeys:Origins unset, every route that
// runs a ceremony answers 404 Passkeys.Off; the list, rename and both removals
// still work (G7.8); a serving start warns when passkeys are stored (G7.12).
// Its own factory and database, so the warning sees only these passkeys.
public sealed class PasskeysOffTests(PasskeysOffTests.OffFactory factory) : IClassFixture<PasskeysOffTests.OffFactory>
{
    public sealed class OffFactory : CluckworkWebApplicationFactory
    {
        public SecondFactorFactory.LogSink Sink { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.AddSingleton<ILogEventSink>(Sink));
        }

        public int OffWarnings => Sink.Events.Count(e => e.Level == LogEventLevel.Warning
            && e.Properties.TryGetValue("Event", out var name) && name is ScalarValue { Value: PasskeysOffWarning.EventName });
    }

    private const string PasskeysPath = "/api/v1/auth/passkeys";

    private async Task<(Guid AccountId, Guid UserId, HttpClient Client)> SignedInAsync(string email)
    {
        var accountId = await factory.SeedAccountWithUserAsync(email, asAdmin: false);
        var userId = await factory.WithTenantScopeAsync(accountId, db => db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync());
        return (accountId, userId, factory.CreateAuthedClient((await factory.LoginAsync(email)).AccessToken));
    }

    private static async Task<string?> GrantAsync(HttpClient client, string? code = null)
    {
        var response = await client.PostWithKeyAsync("/api/v1/auth/step-up", Guid.NewGuid().ToString(),
            code is null ? new { password = TestHarness.Password } : new { password = TestHarness.Password, code });
        return response.IsSuccessStatusCode
            ? (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()
            : null;
    }

    private static async Task<(HttpStatusCode, string?)> TitleOf(HttpResponseMessage response) =>
        (response.StatusCode, response.Content.Headers.ContentLength == 0 ? null
            : (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());

    // G7.8: the routes that run a ceremony answer 404.
    [Fact]
    public async Task TheCeremonyRoutes_Answer404_WhilePasskeysAreOff()
    {
        var (_, _, client) = await SignedInAsync($"off-{Guid.NewGuid():N}@test.local");
        var grant = await GrantAsync(client);
        var finish = new HttpRequestMessage(HttpMethod.Post, PasskeysPath)
        {
            Content = JsonContent.Create(new { ceremony = Guid.NewGuid(), credential = new { id = "AQID" }, name = "Phone" }),
        };
        finish.Headers.Add(AuthEndpoints.StepUpHeaderName, grant);

        Assert.Equal((HttpStatusCode.NotFound, "Passkeys.Off"), await TitleOf(await client.PostAsync(PasskeysPath + "/options", null)));
        Assert.Equal((HttpStatusCode.NotFound, "Passkeys.Off"), await TitleOf(await client.SendAsync(finish)));
    }

    // Decision 5: a finish while off answers 404 before the grant is read, so a
    // caller with no grant gets 404 too, and a grant sent with it stays unspent.
    [Fact]
    public async Task ThePasskeyFinish_Answers404BeforeTheGrant_WhilePasskeysAreOff()
    {
        var email = $"off-{Guid.NewGuid():N}@test.local";
        var (accountId, userId, _) = await SignedInAsync(email);
        var factor = await factory.EnrolTwoFactorAsync(email);
        var client = factory.CreateAuthedClient((await factory.LoginAsync(email)).AccessToken);
        await factory.SeedPasskeysAsync(accountId, userId, 1);
        var grant = await GrantAsync(client, factor.RecoveryCodes[0]);
        HttpRequestMessage Finish(string? withGrant)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, PasskeysPath)
            {
                Content = JsonContent.Create(new { ceremony = Guid.NewGuid(), credential = new { id = "AQID" }, name = "Phone" }),
            };
            if (withGrant is not null) request.Headers.Add(AuthEndpoints.StepUpHeaderName, withGrant);
            return request;
        }

        var withoutGrant = await client.SendAsync(Finish(null));
        var withGrant = await client.SendAsync(Finish(grant));
        var id = (await client.GetFromJsonAsync<JsonElement>(PasskeysPath)).GetProperty("passkeys")[0].GetProperty("id").GetString();
        var removal = new HttpRequestMessage(HttpMethod.Delete, $"{PasskeysPath}/{id}");
        removal.Headers.Add(AuthEndpoints.StepUpHeaderName, grant);

        Assert.Equal((HttpStatusCode.NotFound, "Passkeys.Off"), await TitleOf(withoutGrant));
        Assert.Equal((HttpStatusCode.NotFound, "Passkeys.Off"), await TitleOf(withGrant));
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(removal)).StatusCode);
    }

    // G7.8: the list, rename and a user's own removal work while off. The user
    // has the app and steps up with the password and a code, so #1443 leaves
    // this case as it is.
    [Fact]
    public async Task TheListRenameAndOwnRemoval_WorkWhilePasskeysAreOff()
    {
        var email = $"off-{Guid.NewGuid():N}@test.local";
        var (accountId, userId, _) = await SignedInAsync(email);
        var factor = await factory.EnrolTwoFactorAsync(email);
        var client = factory.CreateAuthedClient((await factory.LoginAsync(email)).AccessToken);
        await factory.SeedPasskeysAsync(accountId, userId, 2);

        var listed = await client.GetAsync(PasskeysPath);
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        var body = await listed.Content.ReadFromJsonAsync<JsonElement>();
        var ids = body.GetProperty("passkeys").EnumerateArray().Select(p => p.GetProperty("id").GetString()!).ToList();
        var renamed = await client.PatchAsJsonAsync($"{PasskeysPath}/{ids[0]}", new { name = "Old phone" });
        var removal = new HttpRequestMessage(HttpMethod.Delete, $"{PasskeysPath}/{ids[1]}");
        removal.Headers.Add(AuthEndpoints.StepUpHeaderName, await GrantAsync(client, factor.RecoveryCodes[0]));
        var removed = await client.SendAsync(removal);

        Assert.False(body.GetProperty("enabled").GetBoolean());
        Assert.Equal(2, ids.Count);
        Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Equal((1, 1), await factory.PasskeyStateAsync(accountId, userId));
        Assert.Equal("Old phone", await factory.WithTenantScopeAsync(accountId, db => db.UserPasskeys
            .Where(p => p.UserId == userId).Select(p => p.Data.Name).SingleAsync()));
        Assert.Equal(ids[0], WebEncoders.Base64UrlEncode(await factory.WithTenantScopeAsync(accountId, db => db.UserPasskeys
            .Where(p => p.UserId == userId).Select(p => p.CredentialId).SingleAsync())));
    }

    // G7.12: the Owner's removal works while off and zeroes the count.
    [Fact]
    public async Task AnOwnersRemoval_WorksWhilePasskeysAreOff()
    {
        var ownerEmail = $"off-owner-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(ownerEmail);
        var owner = factory.CreateAuthedClient((await factory.LoginAsync(ownerEmail)).AccessToken);
        var email = $"off-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, email, Roles.Manager);
        var userId = await factory.WithTenantScopeAsync(accountId, db => db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync());
        await factory.SeedPasskeysAsync(accountId, userId, 3);
        var removal = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/users/{userId}/passkeys");
        removal.Headers.Add(AuthEndpoints.StepUpHeaderName, await GrantAsync(owner));
        removal.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await owner.SendAsync(removal);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal((0, 0), await factory.PasskeyStateAsync(accountId, userId));
    }

    // G7.12: a serving start with passkeys off and one stored warns once,
    // naming clear-passkeys; with passkeys on it says nothing.
    [Fact]
    public async Task AServingStart_WarnsOnce_WhenPasskeysAreOffAndStored_AndNotWhenOn()
    {
        var (accountId, userId, _) = await SignedInAsync($"off-{Guid.NewGuid():N}@test.local");
        await factory.SeedPasskeysAsync(accountId, userId, 1);

        var before = factory.OffWarnings;
        using (var off = factory.WithWebHostBuilder(_ => { }))
            off.CreateClient().Dispose();
        var afterOff = factory.OffWarnings;
        using (var on = factory.WithWebHostBuilder(builder => builder
            .UseSetting("Passkeys:Origins:0", "https://localhost").UseSetting("AllowedHosts", "cluckwork.test;localhost")))
            on.CreateClient().Dispose();

        Assert.Equal(before + 1, afterOff);
        Assert.Equal(afterOff, factory.OffWarnings);
        var warning = factory.Sink.Events.Last(e => e.Properties.TryGetValue("Event", out var name)
            && name is ScalarValue { Value: PasskeysOffWarning.EventName });
        Assert.Contains("clear-passkeys", warning.RenderMessage(), StringComparison.Ordinal);
    }

    // G7.12: with none stored, a serving start says nothing. A fresh factory,
    // so its database has no passkey.
    [Fact]
    public async Task AServingStart_SaysNothing_WhenNoPasskeyIsStored()
    {
        await using var fresh = new OffFactory();
        await fresh.InitializeAsync();
        fresh.CreateClient().Dispose();

        Assert.Equal(0, fresh.OffWarnings);
        Assert.True(fresh.Sink.Events.Count > 0, "the fresh host logged nothing at all, so the sink saw nothing");
    }

    // G7.12: a one-shot verb never starts the host, so it never registers the warning.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OnlyAServingProcess_RegistersTheWarning(bool serving)
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:PublicKeyPem"] = TestJwtKeys.PublicKeyPem,
            ["Jwt:PrivateKeyPem"] = TestJwtKeys.PrivateKeyPem,
        }).Build();

        services.AddCluckworkIdentity(configuration, new FakeEnvironment("Testing"), serving ? ProcessRole.Serving : ProcessRole.OneShot);

        Assert.Equal(serving, services.Any(d => d.ImplementationType == typeof(PasskeysOffWarning)));
    }

    // G7.8: the boot check, one entry at a time. Called directly, so every
    // branch runs without a host; the boot case below proves it runs at boot.
    [Theory]
    [InlineData("http://localhost", "Production", "must use https")]
    [InlineData("http://localhost", "Testing", "must use https")]
    [InlineData("https://*.farm.example", "Production", "must be an origin")]
    [InlineData("https://farm.example/app", "Production", "must be an origin")]
    [InlineData("https://farm.example/", "Production", "must be an origin")]
    [InlineData("https://farm.example?x=1", "Production", "must be an origin")]
    [InlineData("https://user@farm.example", "Production", "must be an origin")]
    [InlineData("farm.example", "Production", "must be an origin")]
    [InlineData("https://elsewhere.example", "Production", "which AllowedHosts does not list")]
    [InlineData("https://farm.example", "Production", null)]
    [InlineData("https://FARM.example:8443", "Production", null)]
    [InlineData("http://localhost:5173", "Development", null)]
    public void EachOriginEntry_IsCheckedAtBoot(string origin, string environment, string? refusal)
    {
        var thrown = Record.Exception(() => EnsurePasskeyOrigins(environment, new()
        {
            ["Passkeys:Origins:0"] = origin,
            ["AllowedHosts"] = "farm.example;localhost",
        }));

        if (refusal is null)
            Assert.Null(thrown);
        else
            Assert.Contains(refusal, Assert.IsType<InvalidOperationException>(thrown).Message, StringComparison.Ordinal);
    }

    // G7.8: passkeys are on at a host only when exactly one origin names it
    // (IsOnFor), so two entries at one host would leave it off with no log.
    // Uri.Host lower-cases an ASCII host, so the FARM row checks the port, not
    // the comparer; the à and À row keeps its case and needs OrdinalIgnoreCase.
    [Theory]
    [InlineData("https://farm.example", "https://farm.example:443", "farm.example")]
    [InlineData("https://farm.example", "https://FARM.example:8443", "farm.example")]
    [InlineData("https://farm.example", "https://farm.example", "farm.example")]
    [InlineData("https://aàb.example", "https://aÀb.example", "aÀb.example")]
    [InlineData("https://farm.example", "https://localhost", null)]
    public void TwoOriginEntriesAtOneHost_FailTheBootCheck(string first, string second, string? host)
    {
        var thrown = Record.Exception(() => EnsurePasskeyOrigins("Production", new()
        {
            ["Passkeys:Origins:0"] = first,
            ["Passkeys:Origins:1"] = second,
            ["AllowedHosts"] = "farm.example;localhost;aàb.example;aÀb.example",
        }));

        if (host is null)
        {
            Assert.Null(thrown);
            return;
        }
        var message = Assert.IsType<InvalidOperationException>(thrown).Message;
        Assert.Contains($"'{first}'", message, StringComparison.Ordinal);
        Assert.Contains($"'{second}'", message, StringComparison.Ordinal);
        Assert.Contains($"'{host}'", message, StringComparison.Ordinal);
    }

    // G7.8: a single value binds no origin, so it is refused rather than
    // leaving passkeys silently off; an empty one is an empty list.
    [Fact]
    public void ASingleOriginValueInsteadOfAList_FailsTheBootCheck()
    {
        var thrown = Record.Exception(() => EnsurePasskeyOrigins("Production", new()
        {
            ["Passkeys:Origins"] = "https://farm.example",
            ["AllowedHosts"] = "farm.example",
        }));

        Assert.Contains("Passkeys:Origins must be a list", Assert.IsType<InvalidOperationException>(thrown).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AnUnsetOrEmptyList_PassesTheBootCheck(string? value)
    {
        var settings = new Dictionary<string, string?> { ["AllowedHosts"] = "farm.example" };
        if (value is not null) settings["Passkeys:Origins"] = value;

        Assert.Null(Record.Exception(() => EnsurePasskeyOrigins("Production", settings)));
    }

    [Fact]
    public void ABadOrigin_FailsAServingBoot()
    {
        using var host = factory.WithWebHostBuilder(builder => builder.UseSetting("Passkeys:Origins:0", "http://cluckwork.test"));
        var thrown = Assert.ThrowsAny<Exception>(() => host.CreateClient());

        var chain = new List<Exception>();
        for (Exception? e = thrown; e is not null; e = e.InnerException) chain.Add(e);
        Assert.Contains(chain, e => e is InvalidOperationException && e.Message.Contains("Passkeys:Origins:0 must use https", StringComparison.Ordinal));
    }

    private static void EnsurePasskeyOrigins(string environment, Dictionary<string, string?> settings) =>
        CluckworkIdentityServiceCollectionExtensions.EnsurePasskeyOrigins(
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), new FakeEnvironment(environment));

    private sealed class FakeEnvironment(string name) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Cluckwork.Api";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
