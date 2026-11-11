using System.Reflection;
using System.Text.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Api.Modules.Access.Auth;
using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.Modules.Access.Identity;
using Microsoft.AspNetCore.Identity;

namespace Cluckwork.Api.IntegrationTests;

// G3.29 (#1397): every type in the four product assemblies that carries a
// credential prints only "[redacted]" or its type name. CredentialTypes finds
// them from what each type declares, so a new one joins the walk by itself.
public sealed class RedactedCredentialTypesTests
{
    // The exact size of the found set. A drop means a type or a word left the
    // walk; lowering this is a reviewed change.
    private const int Floor = 56;

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

    [Fact]
    public void EveryExclusion_HasAReason_AndExcusesAMemberTheWalkSees()
    {
        var walked = CredentialTypes.Walk(Product).ToList();

        Assert.Equal(CredentialTypes.Excused.Length, CredentialTypes.Excused.DistinctBy(excuse => (excuse.On, excuse.Member)).Count());
        foreach (var excuse in CredentialTypes.Excused)
        {
            Assert.False(string.IsNullOrWhiteSpace(excuse.Reason), $"{excuse.On}.{excuse.Member} has no reason");
            Assert.True(walked.Any(type => CredentialTypes.Members(type).Any(member =>
                    CredentialTypes.NamesACredential(member.Name) && excuse.Covers(type, member))),
                $"{excuse.On}.{excuse.Member} excuses no member the walk sees; delete the row");
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
    public void ANewCredentialType_IsFound_AndFailsUntilItRedacts(Type type)
    {
        Assert.Contains(type, CredentialTypes.Find([typeof(RedactedCredentialTypesTests).Assembly]));
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => CredentialTypes.AssertRedacted(type));
    }

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

    // The override changes ToString only: the step-up response still
    // serializes its token and expiry under the same names.
    [Fact]
    public void ARedactedResponse_SerializesAsBefore()
    {
        var token = Guid.NewGuid().ToString("N");
        var expiresAt = DateTimeOffset.UtcNow;

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new StepUpResponse(token, expiresAt), JsonSerializerOptions.Web));

        Assert.Equal(["token", "expiresAt"], json.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(token, json.RootElement.GetProperty("token").GetString());
        Assert.Equal(expiresAt, json.RootElement.GetProperty("expiresAt").GetDateTimeOffset());
    }
}
