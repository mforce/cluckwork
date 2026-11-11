using System.Collections;
using System.Reflection;
using System.Text.Json;
using Cluckwork.Api.Configuration;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Api.Modules.Access.Auth;
using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.Jobs;
using Cluckwork.Infrastructure.Modules.Access.Identity;
using Microsoft.AspNetCore.Identity;

namespace Cluckwork.Api.IntegrationTests;

// G3.29 (#1397): every type in the four product assemblies that carries a
// credential prints only "[redacted]" or its type name. CredentialTypes finds
// them from what each type declares, so a new one joins the walk by itself.
public sealed class RedactedCredentialTypesTests
{
    // A lower bound, the size of the found set when it was last measured. An
    // unrelated new match adds slack, so a lost word or marker is pinned by a
    // named type or a fixture below, not by this number.
    private const int Floor = 59;

    private static readonly Assembly[] Product =
    [
        typeof(Error).Assembly,
        typeof(TokenPair).Assembly,
        typeof(TwoFactorCredentials).Assembly,
        typeof(StepUpRequest).Assembly,
    ];

    [Fact]
    public void EveryCredentialType_PrintsOnlyRedacted()
    {
        var found = CredentialTypes.Find(Product);

        Assert.True(found.Count >= Floor, $"the walk found {found.Count} credential types, below its floor of {Floor}");
        var failures = found.Select(type => (type, error: Record.Exception(() => CredentialTypes.AssertRedacted(type))))
            .Where(result => result.error is not null)
            .Select(result => $"{result.type}: {result.error!.Message}")
            .ToList();
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    // The types only the [Credential] marker finds, named, so the floor's slack
    // cannot hide a lost marker.
    [Fact]
    public void EveryMarkedType_IsFound()
    {
        var found = CredentialTypes.Find(Product);

        Assert.Contains(typeof(SecondFactorCode.Totp), found);
        Assert.Contains(typeof(SecondFactorCode.Recovery), found);
        Assert.Contains(typeof(LeaderLeaseConnectionString), found);
        Assert.Contains(typeof(OtlpOptions), found);
    }

    [Fact]
    public void EveryExclusion_HasAReason_AndExcusesAMemberTheWalkSees()
    {
        var walked = CredentialTypes.Walk(Product).ToList();

        Assert.Equal(CredentialTypes.Excused.Length, CredentialTypes.Excused.DistinctBy(excuse => (excuse.On, excuse.Member, excuse.Holds)).Count());
        foreach (var excuse in CredentialTypes.Excused)
        {
            Assert.False(string.IsNullOrWhiteSpace(excuse.Reason), $"{excuse.On}.{excuse.Member} as {excuse.Holds} has no reason");
            Assert.True(walked.Any(type => CredentialTypes.Members(type).Any(member =>
                    CredentialTypes.NamesACredential(member.Name) && excuse.Covers(type, member))),
                $"{excuse.On}.{excuse.Member} as {excuse.Holds} excuses no member the walk sees; delete the row");
        }
        foreach (var (printer, reason) in CredentialTypes.ReviewedToStrings)
        {
            Assert.False(string.IsNullOrWhiteSpace(reason), $"{printer} has no reason");
            Assert.True(CredentialTypes.Find(Product).Any(type => type.GetMethod(nameof(ToString), Type.EmptyTypes)!.DeclaringType == printer),
                $"no credential type prints through {printer}; delete the row");
        }
    }

    // The reviewed IdentityUser ToString, run: a user with a password hash and
    // a security stamp prints its user name only.
    [Fact]
    public void AnApplicationUser_PrintsItsUserNameOnly()
    {
        var user = new ApplicationUser
        {
            UserName = $"user-{Guid.NewGuid():N}",
            PasswordHash = Guid.NewGuid().ToString("N"),
            SecurityStamp = Guid.NewGuid().ToString("N"),
        };

        Assert.Equal(user.UserName, user.ToString());
    }

    private sealed record NewPassword(string Password);

    private sealed record NewToken(string Token);

    private sealed record NewStepUpToken(Guid UserId, string? StepUpToken);

    private sealed record NewRecoveryCode(string RecoveryCode);

    private sealed record Envelope<T>(string Token, T Body);

    private record RedactedBase(string Password)
    {
        public override string ToString() => "[redacted]";
    }

    // Not sealed in the base, so the compiler generates a ToString here that
    // prints Password again.
    private sealed record PrintsAgain(string Password, int Extra) : RedactedBase(Password);

    [Credential]
    private record MarkedBase(string Value)
    {
        public override string ToString() => "[redacted]";
    }

    private sealed record MarkedDerived(string Value, int Extra) : MarkedBase(Value);

    private sealed class PrintsTheHash : IdentityUser<Guid>
    {
        public override string ToString() => PasswordHash ?? "";
    }

    private sealed class CapturesAKey(string signingKey)
    {
        public override string ToString() => signingKey;
    }

    // Keeps object's ToString, but interpolation calls the format method.
    private sealed class FormatsItsPassword : IFormattable
    {
        public string? Password { get; init; }
        public string ToString(string? format, IFormatProvider? formatProvider) => Password ?? "";
    }

    // Each found by one rule the floor alone would not pin.
    private sealed record NamedChallenge(string Challenge);

    private sealed record NamedKey(string Key);

    private sealed record NamedCode(string Code);

    private sealed record AcronymKey(string APIKey);

    private sealed record AllCaps(string JWT);

    private sealed record UppercaseTokens(string TOKENS);

    private sealed class LowercaseField(string value)
    {
        private readonly string token = value;
        public override string ToString() => token;
    }

    // A framework ToString that is not reviewed.
    private sealed class TokenProblem(string token) : Exception(token)
    {
        public string Token { get; } = token;
    }

    // An inherited ToString gets the IL check too.
    private abstract record PrintingBase(string Token)
    {
        public sealed override string ToString() => Token;
    }

    private sealed record InheritsAPrinter(string Token) : PrintingBase(Token);

    // An excused name holding something other than what its row says.
    private sealed record Leftovers(IReadOnlyList<string> RecoveryCodesLeft);

    // Loggers enumerate a sequence and never call its ToString.
    private sealed class CodeSet(IReadOnlyList<string> recoveryCodes) : IEnumerable<string>
    {
        public IReadOnlyList<string> RecoveryCodes { get; } = recoveryCodes;
        public IEnumerator<string> GetEnumerator() => RecoveryCodes.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed record RedactedCodeSet(IReadOnlyList<string> RecoveryCodes) : IEnumerable<string>
    {
        public override string ToString() => "[redacted]";
        public IEnumerator<string> GetEnumerator() => RecoveryCodes.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // The acceptance cases: each new type joins the walk with no row anywhere
    // and fails the check.
    [Theory]
    [InlineData(typeof(NewPassword))]
    [InlineData(typeof(NewToken))]
    [InlineData(typeof(NewStepUpToken))]
    [InlineData(typeof(NewRecoveryCode))]
    [InlineData(typeof(Envelope<>))]
    [InlineData(typeof(PrintsAgain))]
    [InlineData(typeof(MarkedDerived))]
    [InlineData(typeof(PrintsTheHash))]
    [InlineData(typeof(CapturesAKey))]
    [InlineData(typeof(FormatsItsPassword))]
    [InlineData(typeof(CodeSet))]
    [InlineData(typeof(RedactedCodeSet))]
    [InlineData(typeof(Leftovers))]
    [InlineData(typeof(NamedChallenge))]
    [InlineData(typeof(NamedKey))]
    [InlineData(typeof(NamedCode))]
    [InlineData(typeof(AcronymKey))]
    [InlineData(typeof(AllCaps))]
    [InlineData(typeof(UppercaseTokens))]
    [InlineData(typeof(LowercaseField))]
    [InlineData(typeof(TokenProblem))]
    [InlineData(typeof(InheritsAPrinter))]
    public void ANewCredentialType_IsFound_AndFailsUntilItRedacts(Type type)
    {
        Assert.Contains(type, CredentialTypes.Find([typeof(RedactedCredentialTypesTests).Assembly]));
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => CredentialTypes.AssertRedacted(type));
    }

    // One name per word, each matched by that word alone, then the plural,
    // acronym, lowercase and captured-parameter forms.
    [Theory]
    [InlineData("NewPassword")]
    [InlineData("AccessToken")]
    [InlineData("ClientSecret")]
    [InlineData("Challenge")]
    [InlineData("Grant")]
    [InlineData("ApiKey")]
    [InlineData("AuthCode")]
    [InlineData("SecurityStamp")]
    [InlineData("PrivatePem")]
    [InlineData("OtpValue")]
    [InlineData("TotpSeed")]
    [InlineData("Verifier")]
    [InlineData("Nonce")]
    [InlineData("Signature")]
    [InlineData("SessionCookie")]
    [InlineData("Credential")]
    [InlineData("Proof")]
    [InlineData("DbPwd")]
    [InlineData("Bearer")]
    [InlineData("Passphrase")]
    [InlineData("Passcode")]
    [InlineData("Pin")]
    [InlineData("Salt")]
    [InlineData("Jwt")]
    [InlineData("RecoveryCodes")]
    [InlineData("APIKey")]
    [InlineData("JWT")]
    [InlineData("TOKENS")]
    [InlineData("PASSWORDS")]
    [InlineData("token")]
    [InlineData("<signingKey>P")]
    public void TheNameMatcher_ReadsEachWord(string name) =>
        Assert.True(CredentialTypes.NamesACredential(name), name);

    [Theory]
    [InlineData("DisplayName")]
    [InlineData("Pinned")]
    [InlineData("Saltwater")]
    [InlineData("Codename")]
    public void TheNameMatcher_ReadsWholeWordsOnly(string name) =>
        Assert.False(CredentialTypes.NamesACredential(name), name);

    // A redacted record and a class that keeps object's ToString pass.
    private sealed record Redacted(string Password)
    {
        public override string ToString() => "[redacted]";
    }

    private sealed class PrintsItsTypeName
    {
        public string? Password { get; init; }
    }

    [Theory]
    [InlineData(typeof(Redacted))]
    [InlineData(typeof(PrintsItsTypeName))]
    public void ARedactingType_Passes(Type type)
    {
        Assert.True(CredentialTypes.IsCredentialType(type));
        CredentialTypes.AssertRedacted(type);
    }

    // The override changes ToString only: the step-up and access-token
    // responses still serialize their token and expiry under the same names.
    [Fact]
    public void ARedactedResponse_SerializesAsBefore()
    {
        var token = Guid.NewGuid().ToString("N");
        var expiresAt = DateTimeOffset.UtcNow;

        using var stepUp = JsonDocument.Parse(JsonSerializer.Serialize(new StepUpResponse(token, expiresAt), JsonSerializerOptions.Web));
        using var access = JsonDocument.Parse(JsonSerializer.Serialize(new AccessTokenResponse(token, expiresAt), JsonSerializerOptions.Web));

        Assert.Equal(["token", "expiresAt"], stepUp.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(token, stepUp.RootElement.GetProperty("token").GetString());
        Assert.Equal(expiresAt, stepUp.RootElement.GetProperty("expiresAt").GetDateTimeOffset());
        Assert.Equal(["accessToken", "accessTokenExpiry"], access.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(token, access.RootElement.GetProperty("accessToken").GetString());
        Assert.Equal(expiresAt, access.RootElement.GetProperty("accessTokenExpiry").GetDateTimeOffset());
    }
}
