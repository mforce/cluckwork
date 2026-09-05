namespace Cluckwork.Application.Tests.TenantBypass;

// #632 — the identity scheme's own guard, and #698's review round is why it
// exists as a separate file: all three findings there were the SAME failure —
// the identity not changing when the query did — and none of them were
// reachable from the real tree, because no src/ site happens to have that
// shape today. A scheme whose only test is "the current tree still passes"
// cannot see them.
public sealed class FilterFreeSetIdentityTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("t632-identity-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // The scanner needs a repo-shaped root: it derives the relative path from
    // the PARENT of the src root, so the fixture mirrors that layout.
    private string WriteSource(string body)
    {
        var src = Path.Combine(_root, "src");
        Directory.CreateDirectory(src);
        var file = Path.Combine(src, "Fixture.cs");
        File.WriteAllText(file, body);
        return src;
    }

    private string IdentityOf(string body)
    {
        var occurrences = GuardScanner.ScanFilterFreeSet(WriteSource(body), ["Users"]);
        var single = Assert.Single(occurrences);
        return GuardScanner.FilterFreeSetIdentity(single);
    }

    private const string PropertyShell = """
        namespace Fixture;
        public sealed class Repo
        {
            private readonly AppDbContext db = null!;
            public object Query => db.Users.Where(u => u.Email == "{VALUE}");
        }
        """;

    // #698 finding 1. An expression-bodied property has neither a statement
    // nor a method ancestor, so the scope fell through to the ACCESS — the
    // signature hashed the bare `db.Users` and the predicate was invisible.
    [Fact]
    public void ExpressionBodiedProperty_EditingThePredicateChangesTheIdentity()
    {
        var before = IdentityOf(PropertyShell.Replace("{VALUE}", "a@farm.test"));
        var after = IdentityOf(PropertyShell.Replace("{VALUE}", "b@farm.test"));

        Assert.NotEqual(before, after);
    }

    // …and the symbol has to name the property. Every expression-bodied query
    // in one file used to key as the same "<Fixture.cs>.<top-level>", so two
    // of them could not be told apart even with a working signature.
    [Fact]
    public void ExpressionBodiedProperty_IsNamedByItsProperty()
    {
        var identity = IdentityOf(PropertyShell.Replace("{VALUE}", "a@farm.test"));

        Assert.Contains("Fixture.Repo.Query", identity, StringComparison.Ordinal);
        Assert.DoesNotContain("<top-level>", identity, StringComparison.Ordinal);
    }

    private const string UrlShell = """
        namespace Fixture;
        public sealed class Repo
        {
            private readonly AppDbContext db = null!;
            public object Find()
            {
                return db.Users.Where(u => u.Website == "https://{HOST}/path").ToList();
            }
        }
        """;

    // #698 finding 2. Stripping comments with a regex read the `//` inside a
    // URL literal as the start of a comment and deleted the rest of the
    // statement — so everything after it, the predicate included, stopped
    // affecting the signature.
    [Fact]
    public void StringLiteralContainingSlashes_StillParticipatesInTheIdentity()
    {
        var before = IdentityOf(UrlShell.Replace("{HOST}", "one.example"));
        var after = IdentityOf(UrlShell.Replace("{HOST}", "two.example"));

        Assert.NotEqual(before, after);
    }

    // Whitespace inside a literal is data, not formatting.
    [Fact]
    public void WhitespaceInsideALiteral_IsPartOfTheIdentity()
    {
        const string shell = """
            namespace Fixture;
            public sealed class Repo
            {
                private readonly AppDbContext db = null!;
                public object Find() => db.Users.Where(u => u.Name == "{VALUE}");
            }
            """;

        Assert.NotEqual(
            IdentityOf(shell.Replace("{VALUE}", "two  spaces")),
            IdentityOf(shell.Replace("{VALUE}", "two spaces")));
    }

    // The positive control, and the whole point of #632: the things that used
    // to move a site must not move it. Without this, "the identity changed"
    // tests above would pass on a scheme that simply hashed everything.
    [Fact]
    public void CommentsAndFormatting_DoNotChangeTheIdentity()
    {
        const string plain = """
            namespace Fixture;
            public sealed class Repo
            {
                private readonly AppDbContext db = null!;
                public object Find()
                {
                    return db.Users.Where(u => u.Email == "a@farm.test").ToList();
                }
            }
            """;
        const string commented = """
            namespace Fixture;
            public sealed class Repo
            {
                private readonly AppDbContext db = null!;
                public object Find()
                {
                    // A comment above the query.
                    return db.Users /* and one inside it */
                        .Where(u => u.Email == "a@farm.test")
                        .ToList();
                }
            }
            """;

        Assert.Equal(IdentityOf(plain), IdentityOf(commented));
    }

    // The in-statement ordinal: one statement, the same set twice. Nothing
    // about the text can separate them, so the identity must.
    [Fact]
    public void OneStatementQueryingTheSameSetTwice_YieldsTwoIdentities()
    {
        const string body = """
            namespace Fixture;
            public sealed class Repo
            {
                private readonly AppDbContext db = null!;
                public object Find()
                {
                    return db.Users.Take(1).Concat(db.Users.Take(2)).ToList();
                }
            }
            """;

        var identities = GuardScanner.ScanFilterFreeSet(WriteSource(body), ["Users"])
            .Select(GuardScanner.FilterFreeSetIdentity)
            .ToList();

        Assert.Equal(2, identities.Count);
        Assert.Equal(2, identities.Distinct(StringComparer.Ordinal).Count());
    }
}
