using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Domain.Modules.Farm.Contracts;
using Cluckwork.Infrastructure.Modules.Access.Identity;
using Microsoft.AspNetCore.Identity;

namespace Cluckwork.Api.IntegrationTests;

// #1278 G3.17, G3.18, G3.29, G3.32: the pieces that need no database.
public sealed class TwoFactorPrimitivesTests
{
    // RFC 6238 Appendix B, SHA-1 rows: the seed is the ASCII "12345678901234567890".
    [Theory]
    [InlineData(59L, 287082)]
    [InlineData(1111111109L, 081804)]
    [InlineData(1111111111L, 050471)]
    [InlineData(1234567890L, 005924)]
    [InlineData(2000000000L, 279037)]
    [InlineData(20000000000L, 353130)]
    public void Compute_MatchesTheRfc6238Sha1Vectors(long unixSeconds, int lastSixDigits)
    {
        var step = TotpMatcher.StepAt(DateTimeOffset.FromUnixTimeSeconds(unixSeconds));
        Assert.Equal(lastSixDigits, TotpMatcher.Compute(Encoding.ASCII.GetBytes("12345678901234567890"), step));
    }

    [Fact]
    public async Task TheMatcher_AcceptsExactlyWhatIdentitysProviderAccepts_AndRefusesThreeStepsOut()
    {
        // More than a second from a step boundary, so both read the same step.
        while (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 30_000 is < 1_000 or > 29_000)
            await Task.Delay(250);

        var store = new KeyStore();
        using var manager = new UserManager<ApplicationUser>(store, null!, null!, null!, null!, null!, null!, null!, null!);
        var provider = new AuthenticatorTokenProvider<ApplicationUser>();
        var user = new ApplicationUser { Id = Guid.NewGuid() };
        var now = DateTimeOffset.UtcNow;
        var current = TotpMatcher.StepAt(now);

        for (var i = 0; i < 200; i++)
        {
            var key = manager.GenerateNewAuthenticatorKey();
            store.Key = key;
            var bytes = TotpMatcher.DecodeBase32(key);
            var window = Enumerable.Range(-2, 5).Select(offset => TotpMatcher.Compute(bytes, current + offset)).ToHashSet();
            for (var offset = -3; offset <= 3; offset++)
            {
                var code = TotpMatcher.Compute(bytes, current + offset);
                var identity = await provider.ValidateAsync("Authenticator", code.ToString("D6", CultureInfo.InvariantCulture), manager, user);
                var matched = TotpMatcher.MatchStep(key, code, long.MinValue, now).Step is not null;
                Assert.Equal(identity, matched);
                // Three steps out matches only a code that collides with one in the window.
                Assert.Equal(Math.Abs(offset) <= 2 || window.Contains(code), matched);
            }
        }
    }

    [Fact]
    public void MatchStep_RefusesTheWatermarkStepAsAReuse_AndAcceptsTheNextStep()
    {
        var key = new KeyStore().NewKey();
        var bytes = TotpMatcher.DecodeBase32(key);
        var now = DateTimeOffset.UtcNow;
        var current = TotpMatcher.StepAt(now);

        var reused = TotpMatcher.MatchStep(key, TotpMatcher.Compute(bytes, current), watermark: current, now);
        var next = TotpMatcher.MatchStep(key, TotpMatcher.Compute(bytes, current + 1), watermark: current, now);

        Assert.Equal(new TotpMatch(null, Reused: true), reused);
        Assert.Equal(new TotpMatch(current + 1, Reused: false), next);
    }

    // G3.29: a record prints its members, so every type that can hold a code, a
    // challenge, a key or a stamp hash overrides ToString.
    [Fact]
    public void EveryCredentialCarryingType_PrintsOnlyRedacted()
    {
        Type[] roots = [typeof(SecondFactorCode), typeof(LoginStep)];
        var types = roots
            .SelectMany(root => root.Assembly.GetTypes().Where(t => t.IsSubclassOf(root) && !t.IsAbstract))
            .Append(typeof(ProofBinding))
            .Append(typeof(EnrolmentStart))
            .Append(typeof(Cluckwork.Api.Modules.Access.Auth.SecondFactorLoginRequest))
            .Append(typeof(TwoFactorCredentials).GetNestedType("PendingPayload", System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("TwoFactorCredentials.PendingPayload is gone; update this walk"))
            .ToList();

        Assert.True(types.Count >= 8, $"the walk found {types.Count} types, below its floor of 8");
        foreach (var type in types)
            Assert.True(RuntimeHelpers.GetUninitializedObject(type).ToString() == "[redacted]", $"{type} prints its members");
    }

    // G3.32. Each case reads the URI back with a URI parser, as an app would.
    [Theory]
    [InlineData("North Farm", "Cluckwork (North Farm)")]
    [InlineData("Eggs: Fresh", "Cluckwork (Eggs  Fresh)")]
    [InlineData("  A&B + C 100% / #1  ", "Cluckwork (A&B + C 100% / #1)")]
    [InlineData("Granja Peñalara", "Cluckwork (Granja Peñalara)")]
    [InlineData("養鶏場", "Cluckwork (養鶏場)")]
    [InlineData(":", "Cluckwork (north-farm)")]
    public void TheUri_ReadsBackToTheIssuerAndTheAccount(string farmName, string issuer)
    {
        var userId = Guid.NewGuid();
        var uri = OtpauthUri.Build(farmName, "north-farm", "ana+eggs@example.com", userId, Secret());

        var parsed = Parse(uri);
        Assert.Equal(issuer, parsed.Issuer);
        Assert.Equal(parsed.Issuer, parsed.LabelIssuer);
        Assert.Equal("ana+eggs@example.com", parsed.Account);
    }

    [Fact]
    public void ANameOfOneLongTextElement_IsCutWhole_AndGetsTheFarmCode()
    {
        var name = "a" + new string('́', 120);
        var parsed = Parse(OtpauthUri.Build(name, "north-farm", "ana@example.com", Guid.NewGuid(), Secret()));

        Assert.Equal("Cluckwork (… · north-farm)", parsed.Issuer);
    }

    [Fact]
    public void ALongName_IsCutToAPrefixOfWholeTextElements_WithinTheBound()
    {
        var name = string.Concat(Enumerable.Repeat("é養", 60));
        var issuer = Parse(OtpauthUri.Build(name, "north-farm", "ana@example.com", Guid.NewGuid(), Secret())).Issuer;

        var kept = issuer["Cluckwork (".Length..issuer.IndexOf('…')];
        Assert.StartsWith(kept, name, StringComparison.Ordinal);
        Assert.NotEmpty(kept);
        Assert.InRange(Uri.EscapeDataString(kept).Length, OtpauthUri.FarmNameCap - 9, OtpauthUri.FarmNameCap);
        Assert.EndsWith("… · north-farm)", issuer, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoFarmsWhoseNamesShareTheirCutPrefix_GetDifferentLabels()
    {
        var shared = new string('x', OtpauthUri.FarmNameCap);
        var first = OtpauthUri.Build(shared + " one", "farm-one", "ana@example.com", Guid.Empty, Secret());
        var second = OtpauthUri.Build(shared + " two", "farm-two", "ana@example.com", Guid.Empty, Secret());

        Assert.NotEqual(Parse(first).Issuer, Parse(second).Issuer);
    }

    [Fact]
    public void ALongEmail_IsCut_AndCarriesTheFullUserId()
    {
        var email = new string('e', OtpauthUri.AccountCap) + "@example.com";
        var first = Parse(OtpauthUri.Build("Farm", "farm", email, Guid.Parse("abcdef00-0000-4000-8000-000000000001"), Secret()));
        var second = Parse(OtpauthUri.Build("Farm", "farm", email, Guid.Parse("abcdef11-1111-4111-8111-111111111111"), Secret()));

        Assert.Equal(new string('e', OtpauthUri.AccountCap) + "… · abcdef00000040008000000000000001", first.Account);
        Assert.NotEqual(first.Account, second.Account);
    }

    // The worst case from the declared caps: each part reaches its cap and both
    // suffixes are present. web/src/test/otpauth-worst-case.txt tracks it, with
    // a placeholder where the secret goes, for the SPA's QR-version check.
    [Fact]
    public void TheWorstCaseUri_Is673Bytes_AndEqualsTheTrackedFixture()
    {
        var secret = Secret();
        var uri = OtpauthUri.Build(
            new string('n', OtpauthUri.FarmNameCap + 1), new string('c', FarmCode.MaxLength),
            new string('e', OtpauthUri.AccountCap + 1), Guid.Empty, secret);
        var parsed = Parse(uri);

        Assert.Equal(673, Encoding.UTF8.GetByteCount(uri));
        Assert.Contains("… · ", parsed.Issuer, StringComparison.Ordinal);
        Assert.Contains("… · ", parsed.Account, StringComparison.Ordinal);

        var placeholder = uri.Replace(secret, new string('_', 32), StringComparison.Ordinal);
        var path = Path.Combine(RepoRoot(), "web", "src", "test", "otpauth-worst-case.txt");
        if (Environment.GetEnvironmentVariable("CLUCKWORK_REGENERATE_OTPAUTH") == "1")
            File.WriteAllText(path, placeholder + "\n");
        Assert.True(File.Exists(path), $"{path} is missing; rerun with CLUCKWORK_REGENERATE_OTPAUTH=1");
        Assert.Equal(placeholder, File.ReadAllText(path).TrimEnd('\n'));
    }

    private static string Secret() => new KeyStore().NewKey();

    private sealed record ParsedUri(string LabelIssuer, string Account, string Issuer);

    private static ParsedUri Parse(string uri)
    {
        Assert.True(Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.Scheme == "otpauth" && parsed.Host == "totp", uri);
        var rest = uri["otpauth://totp/".Length..];
        var query = rest[(rest.IndexOf('?') + 1)..].Split('&')
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]));
        Assert.Equal(["issuer", "secret"], query.Keys.Order());
        var label = Uri.UnescapeDataString(rest[..rest.IndexOf('?')]);
        var colon = label.IndexOf(':');
        return new ParsedUri(label[..colon], label[(colon + 1)..], query["issuer"]);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Cluckwork.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    // Enough of a store for UserManager's authenticator-key reads.
    private sealed class KeyStore : IUserAuthenticatorKeyStore<ApplicationUser>
    {
        public string? Key { get; set; }

        public string NewKey()
        {
            using var manager = new UserManager<ApplicationUser>(this, null!, null!, null!, null!, null!, null!, null!, null!);
            return manager.GenerateNewAuthenticatorKey();
        }

        public Task<string?> GetAuthenticatorKeyAsync(ApplicationUser user, CancellationToken cancellationToken) => Task.FromResult(Key);
        public Task SetAuthenticatorKeyAsync(ApplicationUser user, string key, CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose() { }
        public Task<string> GetUserIdAsync(ApplicationUser user, CancellationToken cancellationToken) => Task.FromResult(user.Id.ToString());
        public Task<string?> GetUserNameAsync(ApplicationUser user, CancellationToken cancellationToken) => Task.FromResult(user.UserName);
        public Task SetUserNameAsync(ApplicationUser user, string? userName, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<string?> GetNormalizedUserNameAsync(ApplicationUser user, CancellationToken cancellationToken) => Task.FromResult(user.NormalizedUserName);
        public Task SetNormalizedUserNameAsync(ApplicationUser user, string? normalizedName, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IdentityResult> DeleteAsync(ApplicationUser user, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ApplicationUser?> FindByIdAsync(string userId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ApplicationUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
