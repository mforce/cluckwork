namespace Cluckwork.Domain.Tests.Accounts;

using Cluckwork.Domain.Accounts;

// #531 — the farm code and the suspend/reactivate lifecycle. Slug validity is an
// INVARIANT (Account.Create throws, like Flock.Create), not a Result failure;
// each rejection below is a distinct guard the migration/login work downstream
// relies on. Suspend/Reactivate must each bump Version (the EF concurrency
// token) — proven at the domain level here and against a real race in
// AccountSlugRaceTests.
public sealed class AccountSlugTests
{
    private static Account Farm(string slug) =>
        Account.Create(Guid.NewGuid(), "Test Farm", slug, "UTC", "USD");

    [Fact]
    public void Create_StoresAValidSlug()
    {
        Assert.Equal("good-farm", Farm("good-farm").Slug);
    }

    [Fact]
    public void Create_TrimsSurroundingWhitespace()
    {
        // Normalization is trim-only; case is NOT folded (see the uppercase
        // rejection below), so the stored value is guaranteed lowercase.
        Assert.Equal("good-farm", Farm("  good-farm  ").Slug);
    }

    [Fact]
    public void Create_TrimsNameAndTimeZoneId()
    {
        var account = Account.Create(
            Guid.NewGuid(), "  Farm  ", "good-farm", "  UTC  ", "USD");

        Assert.Equal("Farm", account.Name);
        Assert.Equal("UTC", account.TimeZoneId);
    }

    [Fact]
    public void TryValidateSlug_RejectsUppercase_WithoutThrowing()
    {
        var result = Account.TryValidateSlug("SECOND-FARM");

        Assert.True(result.IsFailure);
        Assert.Equal("Account.SlugInvalid", result.Error.Code);
    }

    [Theory]
    [InlineData("ab")]              // too short (min 3)
    [InlineData("a")]              // too short
    [InlineData("-farm")]          // leading hyphen
    [InlineData("farm-")]          // trailing hyphen
    [InlineData("Farm")]           // uppercase rejected, not folded
    [InlineData("UPPER")]          // uppercase
    [InlineData("bad_slug")]       // underscore not allowed
    [InlineData("bad slug")]       // space not allowed
    [InlineData("")]               // empty
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 33 chars, too long (max 32)
    public void Create_RejectsAnInvalidSlug(string slug)
    {
        var ex = Assert.Throws<ArgumentException>(() => Farm(slug));
        Assert.Equal("slug", ex.ParamName);
    }

    [Fact]
    public void Create_AcceptsExactlyThirtyTwoChars_AndRejectsThirtyThree()
    {
        Assert.Equal(32, Farm(new string('a', 32)).Slug.Length);
        Assert.Throws<ArgumentException>(() => Farm(new string('a', 33)));
    }

    [Fact]
    public void Create_RejectsEveryReservedSlug()
    {
        // Reserved words are valid in SHAPE, so they must be caught by the
        // reserved branch specifically, not the regex.
        Assert.NotEmpty(Account.ReservedSlugs);
        foreach (var reserved in Account.ReservedSlugs)
        {
            var ex = Assert.Throws<ArgumentException>(() => Farm(reserved));
            Assert.Equal("slug", ex.ParamName);
        }
    }

    [Fact]
    public void NewAccount_IsActive_AtVersionZero()
    {
        var account = Farm("lifecycle-farm");
        Assert.True(account.IsActive);
        Assert.Equal(0, account.Version);
    }

    [Fact]
    public void Suspend_DeactivatesAndBumpsVersion()
    {
        var account = Farm("lifecycle-farm");

        account.Suspend();

        Assert.False(account.IsActive);
        Assert.Equal(1, account.Version);
    }

    [Fact]
    public void Reactivate_ReactivatesAndBumpsVersion()
    {
        var account = Farm("lifecycle-farm");
        account.Suspend();

        account.Reactivate();

        Assert.True(account.IsActive);
        Assert.Equal(2, account.Version);
    }

    // #732 — Rename is the one path that can change an existing farm's code. It returns
    // Result (an invalid or reserved code is an expected failure, not an invariant
    // violation), unlike Create, which keeps the throwing ValidateSlug backstop.
    [Fact]
    public void Rename_ChangesTheSlugAndBumpsVersion()
    {
        var account = Farm("lifecycle-farm");

        var result = account.Rename("renamed-farm");

        Assert.True(result.IsSuccess);
        Assert.Equal("renamed-farm", account.Slug);
        Assert.Equal(1, account.Version);
    }

    [Fact]
    public void Rename_TrimsButDoesNotFoldCase()
    {
        var account = Farm("lifecycle-farm");

        Assert.Equal("renamed-farm", account.Rename("  renamed-farm  ").IsSuccess ? account.Slug : null);
        Assert.True(account.Rename("Renamed-Two").IsFailure);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("-farm")]
    [InlineData("farm-")]
    [InlineData("bad_slug")]
    [InlineData("")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Rename_RejectsAnInvalidSlug_AndChangesNothing(string candidate)
    {
        var account = Farm("lifecycle-farm");

        var result = account.Rename(candidate);

        Assert.True(result.IsFailure);
        Assert.Equal("Account.SlugInvalid", result.Error.Code);
        Assert.Equal("lifecycle-farm", account.Slug);
        Assert.Equal(0, account.Version);
    }

    [Fact]
    public void Rename_RejectsEveryReservedSlug_AndChangesNothing()
    {
        // Reserved codes are valid in SHAPE, so they must be refused by the reserved
        // branch specifically — the same distinction Create_RejectsEveryReservedSlug pins.
        Assert.NotEmpty(Account.ReservedSlugs);
        foreach (var reserved in Account.ReservedSlugs)
        {
            var account = Farm("lifecycle-farm");

            var result = account.Rename(reserved);

            Assert.True(result.IsFailure);
            Assert.Equal("Account.SlugInvalid", result.Error.Code);
            Assert.Equal("lifecycle-farm", account.Slug);
            Assert.Equal(0, account.Version);
        }
    }

    // Renaming to the code the farm already has is a NO-OP, and the no-op must not
    // advance Version: Version is the token a Farm Settings form holds open against, so a
    // command that changed nothing must not make that form fail. Same reasoning as the
    // stateChanged gate in AccountSuspensionService.
    [Fact]
    public void Rename_ToTheSameCode_ChangesNothing_AndDoesNotBumpVersion()
    {
        var account = Farm("lifecycle-farm");

        var result = account.Rename("lifecycle-farm");

        Assert.True(result.IsSuccess);
        Assert.Equal("lifecycle-farm", account.Slug);
        Assert.Equal(0, account.Version);
    }

    [Fact]
    public void Rename_ToTheSameCode_AcceptsTheTrimmedForm()
    {
        var account = Farm("lifecycle-farm");

        var result = account.Rename("  lifecycle-farm  ");

        Assert.True(result.IsSuccess);
        Assert.Equal(0, account.Version);
    }
}
