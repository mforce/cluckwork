namespace Cluckwork.Application.Tests.Architecture
{
    using Microsoft.EntityFrameworkCore;
    using Fixtures = TableOwnerFixtures;

    public sealed class TableOwnerTests
    {
        private static ModuleLedger Ledger() => new(
            [new("Red", "module", ["Cluckwork.Application.Tests.Architecture.TableOwnerFixtures"], []),
             new("Blue", "module", [], ["Cluckwork.Application.Tests.Architecture.TableOwnerFixtures.Blue"])], [], [])
        {
            Tables = [new("Red", "Parents"), new("Blue", "Children")],
            ForeignKeys = [new("FK_Children_Parents_ParentId", "Blue", "Red", "a child references its parent")],
        };

        private static TableOwnerReport Scan(ModuleLedger ledger, Action<ModelBuilder>? configure = null)
        {
            using var context = new FixtureContext(configure);
            return TableOwnerScanner.Scan(context.Model, ledger);
        }

        private static IReadOnlyList<string> Evaluate(ModuleLedger ledger, Action<ModelBuilder>? configure = null) =>
            TableOwnerScanner.Evaluate(Scan(ledger, configure) with { ExpectedTableCountFloor = 2 });

        [Fact]
        public void CompleteLedger_IsGreen()
        {
            Assert.Empty(Evaluate(Ledger()));
            var fk = Assert.Single(Scan(Ledger()).CrossOwnerForeignKeys);
            Assert.Equal(new CrossOwnerForeignKey("FK_Children_Parents_ParentId", "Blue", "Red"), fk);
        }

        [Fact]
        public void MissingTable_HasNoOwner() =>
            Assert.Contains(Evaluate(Ledger() with { Tables = [new("Red", "Parents")] }),
                f => f == "table 'Children' (entity Cluckwork.Application.Tests.Architecture.TableOwnerFixtures.Blue.Models.Child) has no owner");

        [Fact]
        public void TableClaimedByTwoOwners_NamesBoth() =>
            Assert.Contains("table 'Children' claimed by Blue, Red", Evaluate(Ledger() with
                { Tables = [.. Ledger().Tables, new("Red", "Children")] }));

        [Fact]
        public void UnmappedTable_IsStale() =>
            Assert.Contains("stale table row 'Gone' under owner Red", Evaluate(Ledger() with
                { Tables = [.. Ledger().Tables, new("Red", "Gone")] }));

        [Fact]
        public void UnknownTableOwner_IsRegistryError() =>
            Assert.Contains("table-owner registry error: table 'Gone' references unknown owner 'Unknown'",
                Evaluate(Ledger() with { Tables = [.. Ledger().Tables, new("Unknown", "Gone")] }));

        [Fact]
        public void TableRepeatedUnderOneOwner_IsRegistryError() =>
            Assert.Contains("table-owner registry error: table 'Parents' listed twice under owner Red",
                Evaluate(Ledger() with { Tables = [.. Ledger().Tables, new("Red", "Parents")] }));

        [Fact]
        public void NamespaceOwnerMismatch_NamesBothOwners() =>
            Assert.Contains(Evaluate(Ledger() with { Tables = [new("Red", "Parents"), new("Red", "Children")] }),
                f => f.Contains("table 'Children' owner Red disagrees with CLR namespace owner Blue"));

        [Fact]
        public void ReasonedOverride_AllowsNamespaceMismatch() =>
            Assert.Empty(Evaluate(Ledger() with
            {
                Tables = [new("Red", "Parents"), new("Red", "Children")],
                ForeignKeys = [],
                TableOwnerOverrides = [new("Children", "fixture design assigns children to Red")],
            }));

        [Fact]
        public void LongestPrefixAndExactClaims_ResolveDescendantNamespaces() =>
            Assert.Empty(Evaluate(Ledger() with
            {
                Owners = [new("Red", "module", ["Cluckwork.Application.Tests.Architecture"], []),
                    new("Blue", "module", [], ["Cluckwork.Application.Tests.Architecture.TableOwnerFixtures.Blue"])],
            }));

        [Fact]
        public void UnclaimedClrNamespace_FailsClosed() =>
            Assert.Contains(Evaluate(Ledger() with
                { Owners = [new("Red", "module", ["Elsewhere"], []), Ledger().Owners[1]] }),
                f => f.Contains("table 'Parents' owner Red disagrees with CLR namespace owner <unowned>"));

        [Fact]
        public void UndeclaredForeignKey_PrintsJsonRow() =>
            Assert.Contains("undeclared cross-owner foreign key FK_Children_Parents_ParentId from Blue to Red — add " +
                "{\"name\":\"FK_Children_Parents_ParentId\",\"from\":\"Blue\",\"to\":\"Red\",\"reason\":\"\"}",
                Evaluate(Ledger() with { ForeignKeys = [] }));

        [Fact]
        public void RemovedForeignKey_LeavesStaleRow() =>
            Assert.Contains("stale foreign-key row 'FK_Gone' from Blue to Red", Evaluate(Ledger() with
                { ForeignKeys = [.. Ledger().ForeignKeys, new("FK_Gone", "Blue", "Red", "removed")] }));

        [Fact]
        public void ForeignKeyEndpoints_MustMatchModelOwners() =>
            Assert.Contains("foreign-key row 'FK_Children_Parents_ParentId' from Red to Blue disagrees with model owners Blue to Red",
                Evaluate(Ledger() with { ForeignKeys = [new("FK_Children_Parents_ParentId", "Red", "Blue", "wrong direction")] }));

        [Fact]
        public void BlankForeignKeyReason_IsRegistryError() =>
            Assert.Contains("table-owner registry error: foreign-key row 'FK_Children_Parents_ParentId' has a blank name or reason",
                Evaluate(Ledger() with { ForeignKeys = [Ledger().ForeignKeys[0] with { Reason = " " }] }));

        [Theory]
        [InlineData("Red")]
        [InlineData("Blue")]
        public void PlatformOnEitherEnd_IsNotTracked(string platform)
        {
            var ledger = Ledger() with
            {
                Owners = Ledger().Owners.Select(o => o.Name == platform ? o with { Kind = "platform" } : o).ToList(),
                ForeignKeys = [],
            };
            Assert.Empty(Evaluate(ledger));
            Assert.Empty(Scan(ledger).CrossOwnerForeignKeys);
        }

        [Fact]
        public void FewerThanThirtyTables_FailsFloor() =>
            Assert.Contains("walked 2 tables, expected at least 30", TableOwnerScanner.Evaluate(Scan(Ledger())));

        [Fact]
        public void OwnedValuesSharingTable_DoNotAddTablesOrNamespaceClaims()
        {
            var report = Scan(Ledger(), m => m.Entity<Fixtures.Parent>().OwnsOne(p => p.Value));
            Assert.Equal(2, report.WalkedTableCount);
            Assert.Empty(TableOwnerScanner.Evaluate(report with { ExpectedTableCountFloor = 2 }));
        }

        [Fact]
        public void DistinctOwnedTable_IsStillDiscovered() =>
            Assert.Contains(Evaluate(Ledger(), m => m.Entity<Fixtures.Parent>().OwnsOne(p => p.Value,
                owned => owned.ToTable("OwnedValues"))), f => f.StartsWith("table 'OwnedValues' ") && f.EndsWith("has no owner"));

        [Fact]
        public void DistinctShadowJoinTable_IsStillDiscovered() =>
            Assert.Contains(Evaluate(Ledger(), m => m.SharedTypeEntity<Dictionary<string, object>>("Join", e =>
            {
                e.ToTable("Joins");
                e.IndexerProperty<int>("Id");
                e.HasKey("Id");
            })), f => f.StartsWith("table 'Joins' ") && f.EndsWith("has no owner"));

        [Fact]
        public void NonPublicSchema_QualifiesTableName() =>
            Assert.Empty(Evaluate(Ledger() with { Tables = [new("Red", "farm.Parents"), new("Blue", "Children")] },
                m => m.Entity<Fixtures.Parent>().ToTable("Parents", "farm")));

        [Fact]
        public void PublicSchema_UsesUnqualifiedTableName() =>
            Assert.Empty(Evaluate(Ledger(), m => m.Entity<Fixtures.Parent>().ToTable("Parents", "public")));

        [Fact]
        public void ViewOnlyEntity_IsNotATable() =>
            Assert.Empty(Evaluate(Ledger(), m => m.Entity<Fixtures.ViewRow>().HasNoKey().ToView("ViewRows")));

        [Theory]
        [InlineData("\"tables\": {\"Unknown\": []}", "tables references unknown owner 'Unknown'")]
        [InlineData("\"tables\": []", "'tables' must be an object")]
        [InlineData("\"tables\": {\"Red\": 5}", "has no 'Red' array")]
        [InlineData("\"tables\": {\"Red\": [null]}", "blank or non-string entry")]
        [InlineData("\"foreignKeys\": {}", "'foreignKeys' must be an array")]
        [InlineData("\"foreignKeys\": [42]", "foreignKeys[0] is not an object")]
        [InlineData("\"foreignKeys\": [{\"name\": 1}]", "blank or non-string 'name'")]
        [InlineData("\"foreignKeys\": [{\"name\": \"FK\", \"from\": \"Red\", \"to\": \"Blue\", \"reason\": \" \"}]", "blank or non-string 'reason'")]
        [InlineData("\"tableOwnerOverrides\": {}", "'tableOwnerOverrides' must be an array")]
        [InlineData("\"tableOwnerOverrides\": [null]", "tableOwnerOverrides[0] is not an object")]
        [InlineData("\"tableOwnerOverrides\": [{\"table\": \"Parents\"}]", "blank or non-string 'reason'")]
        public void MalformedRows_AreRegistryErrors(string section, string expected)
        {
            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, "{\"owners\":{},\"edges\":[]," + section + "}");
                var ledger = ModuleLedger.Load(path);
                Assert.Contains(ledger.RegistryErrors, f => f.Contains(expected));
                Assert.Contains(Evaluate(ledger), f => f.Contains("registry error") && f.Contains(expected));
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void DuplicateForeignKey_IsRegistryError() =>
            Assert.Contains("table-owner registry error: duplicate foreign-key row 'FK_Children_Parents_ParentId'",
                Evaluate(Ledger() with { ForeignKeys = [.. Ledger().ForeignKeys, Ledger().ForeignKeys[0]] }));

        [Fact]
        public void BlankOverrideReason_IsRegistryError() =>
            Assert.Contains("table-owner registry error: table-owner override 'Children' has a blank reason",
                Evaluate(Ledger() with { TableOwnerOverrides = [new("Children", " ")] }));

        [Fact]
        public void SharedJoinMappingOntoDeclaredTable_DoesNotReclassifyIt()
        {
            var report = Scan(Ledger(), m => m.SharedTypeEntity<Dictionary<string, object>>("Shared", e =>
            {
                e.ToTable("Parents");
                e.IndexerProperty<int>("Id");
                e.HasKey("Id");
                e.HasOne<Fixtures.Parent>().WithOne().HasForeignKey("Shared", "Id");
            }));
            Assert.Equal(2, report.WalkedTableCount);
            Assert.Empty(TableOwnerScanner.Evaluate(report with { ExpectedTableCountFloor = 2 }));
        }

        [Fact]
        public void DuplicateJsonOwnerProperties_PreserveEveryTableClaim()
        {
            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path,
                    """{"owners":{},"edges":[],"tables":{"Red":["First"],"Red":["Second"]}}""");
                Assert.Equal([new("Red", "First"), new TableClaim("Red", "Second")], ModuleLedger.Load(path).Tables);
            }
            finally { File.Delete(path); }
        }

        private sealed class FixtureContext(Action<ModelBuilder>? configure) : DbContext
        {
            protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) => optionsBuilder
                .UseNpgsql("Host=localhost;Database=unreachable;Username=unreachable;Password=unreachable")
                .EnableServiceProviderCaching(false);

            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Fixtures.Parent>().ToTable("Parents").Ignore(p => p.Value);
                modelBuilder.Entity<Fixtures.Blue.Models.Child>().ToTable("Children")
                    .HasOne<Fixtures.Parent>().WithMany().HasForeignKey(c => c.ParentId)
                    .HasConstraintName("FK_Children_Parents_ParentId");
                configure?.Invoke(modelBuilder);
            }
        }
    }
}

namespace Cluckwork.Application.Tests.Architecture.TableOwnerFixtures
{
    public sealed class Parent
    {
        public int Id { get; set; }
        public Blue.OwnedValue Value { get; set; } = new();
    }

    public sealed class ViewRow
    {
        public int Value { get; set; }
    }
}

namespace Cluckwork.Application.Tests.Architecture.TableOwnerFixtures.Blue.Models
{
    public sealed class Child
    {
        public int Id { get; set; }
        public int ParentId { get; set; }
    }

}

namespace Cluckwork.Application.Tests.Architecture.TableOwnerFixtures.Blue
{
    public sealed class OwnedValue
    {
        public string Text { get; set; } = "";
    }
}
