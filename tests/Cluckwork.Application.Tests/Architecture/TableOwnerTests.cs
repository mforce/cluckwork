namespace Cluckwork.Application.Tests.Architecture
{
    using Microsoft.EntityFrameworkCore;
    using Microsoft.EntityFrameworkCore.Diagnostics;
    using Fixtures = TableOwnerFixtures;

    public sealed class TableOwnerTests
    {
        private static ModuleLedger Ledger() => new(
            [new("Red", "module", ["Cluckwork.Application.Tests.Architecture.TableOwnerFixtures"], []),
             new("Blue", "module", [], ["Cluckwork.Application.Tests.Architecture.TableOwnerFixtures.Blue"])], [], [])
        {
            ForeignKeys = [new("Children", "FK_Children_Parents_ParentId", "Blue", "Red", "a child references its parent")],
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
            Assert.Equal(new CrossOwnerForeignKey("Children", "FK_Children_Parents_ParentId", "Blue", "Red"), fk);
        }

        [Fact]
        public void EachTable_IsOwnedByItsEntitysNamespaceOwner() =>
            Assert.Equal(new Dictionary<string, string> { ["Children"] = "Blue", ["Parents"] = "Red" }, Scan(Ledger()).Owners);

        [Fact]
        public void NamespaceClaimedByTwoOwners_NamesBoth() =>
            Assert.Contains("table 'Children' claimed by Blue, Green (entity Cluckwork.Application.Tests.Architecture.TableOwnerFixtures.Blue.Models.Child)",
                Evaluate(Ledger() with
                {
                    Owners = [.. Ledger().Owners, new("Green", "module", ["Cluckwork.Application.Tests.Architecture.TableOwnerFixtures.Blue"], [])],
                }));

        [Fact]
        public void EntityMovedToAnotherOwnersNamespace_ReownsItsTable()
        {
            var moved = Ledger() with { Owners = [Ledger().Owners[0], Ledger().Owners[1] with { ExactNamespaces = ["Elsewhere"] }] };

            Assert.Equal("Red", Scan(moved).Owners["Children"]);
            Assert.Contains("stale foreign-key row 'FK_Children_Parents_ParentId' on Children from Blue to Red", Evaluate(moved));
        }

        [Fact]
        public void ReasonedOverride_DecidesTheOwner()
        {
            var ledger = Ledger() with
            {
                ForeignKeys = [],
                TableOwnerOverrides = [new("Children", "Red", "fixture design assigns children to Red")],
            };

            Assert.Empty(Evaluate(ledger));
            Assert.Equal("Red", Scan(ledger).Owners["Children"]);
        }

        [Fact]
        public void OverrideRestatingTheNamespaceOwner_Fails() =>
            Assert.Contains("table-owner override 'Children' restates its CLR namespace owner Blue",
                Evaluate(Ledger() with { TableOwnerOverrides = [new("Children", "Blue", "redundant")] }));

        [Fact]
        public void OverrideOfUnmappedTable_IsStale() =>
            Assert.Contains("table-owner registry error: stale table-owner override 'Gone'",
                Evaluate(Ledger() with { TableOwnerOverrides = [new("Gone", "Red", "removed")] }));

        [Fact]
        public void DuplicateOverride_IsRegistryError() =>
            Assert.Contains("table-owner registry error: duplicate table-owner override 'Children'",
                Evaluate(Ledger() with { TableOwnerOverrides = [new("Children", "Red", "one"), new("Children", "Red", "two")] }));

        [Fact]
        public void UnknownOverrideOwner_IsRegistryError() =>
            Assert.Contains("table-owner registry error: table-owner override 'Children' references unknown owner 'Unknown'",
                Evaluate(Ledger() with { TableOwnerOverrides = [new("Children", "Unknown", "typo")] }));

        [Fact]
        public void LongestPrefixAndExactClaims_ResolveDescendantNamespaces() =>
            Assert.Empty(Evaluate(Ledger() with
            {
                Owners = [new("Red", "module", ["Cluckwork.Application.Tests.Architecture"], []),
                    new("Blue", "module", [], ["Cluckwork.Application.Tests.Architecture.TableOwnerFixtures.Blue"])],
            }));

        [Fact]
        public void UnclaimedClrNamespace_HasNoOwner() =>
            Assert.Contains("table 'Parents' (entity Cluckwork.Application.Tests.Architecture.TableOwnerFixtures.Parent) has no owner",
                Evaluate(Ledger() with { Owners = [new("Red", "module", ["Elsewhere"], []), Ledger().Owners[1]] }));

        [Fact]
        public void UndeclaredForeignKey_PrintsRow() =>
            Assert.Contains("undeclared cross-owner foreign key FK_Children_Parents_ParentId on Children from Blue to Red — add to " +
                "RealModuleLedger.ForeignKeys: new(\"Children\", \"FK_Children_Parents_ParentId\", \"Blue\", \"Red\", \"\"),",
                Evaluate(Ledger() with { ForeignKeys = [] }));

        [Fact]
        public void RemovedForeignKey_LeavesStaleRow() =>
            Assert.Contains("stale foreign-key row 'FK_Gone' on Children from Blue to Red", Evaluate(Ledger() with
                { ForeignKeys = [.. Ledger().ForeignKeys, new("Children", "FK_Gone", "Blue", "Red", "removed")] }));

        [Fact]
        public void ForeignKeyEndpoints_MustMatchModelOwners() =>
            Assert.Contains("foreign-key row 'FK_Children_Parents_ParentId' on Children from Red to Blue disagrees with model owners Blue to Red",
                Evaluate(Ledger() with { ForeignKeys = [new("Children", "FK_Children_Parents_ParentId", "Red", "Blue", "wrong direction")] }));

        [Fact]
        public void BlankForeignKeyReason_IsRegistryError() =>
            Assert.Contains("table-owner registry error: foreign-key row 'FK_Children_Parents_ParentId' has a blank table, name or reason",
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
        public void SplitTableFragment_IsDiscovered() =>
            Assert.Equal("Red", Scan(Ledger(), m => m.Entity<Fixtures.Parent>()
                .SplitToTable("ParentDetails", t => t.Property(p => p.Name))).Owners["ParentDetails"]);

        [Theory]
        [InlineData(null, "ChildDetails")]
        [InlineData("farm", "farm.ChildDetails")]
        public void SplitForeignKey_BelongsToItsPhysicalFragment(string? schema, string table)
        {
            void SplitChild(ModelBuilder m) => m.Entity<Fixtures.Blue.Models.Child>()
                .SplitToTable("ChildDetails", schema, t => t.Property(c => c.ParentId));
            var ledger = Ledger();
            var report = Scan(ledger, SplitChild);

            Assert.Equal(new CrossOwnerForeignKey(table, "FK_Children_Parents_ParentId", "Blue", "Red"),
                Assert.Single(report.CrossOwnerForeignKeys));
            var failures = TableOwnerScanner.Evaluate(report with { ExpectedTableCountFloor = 2 });
            Assert.Contains("stale foreign-key row 'FK_Children_Parents_ParentId' on Children from Blue to Red", failures);
            Assert.Contains(failures, f => f.StartsWith(
                $"undeclared cross-owner foreign key FK_Children_Parents_ParentId on {table} from Blue to Red"));
            Assert.Empty(Evaluate(ledger with
            {
                ForeignKeys = [new(table, "FK_Children_Parents_ParentId", "Blue", "Red", "fragment holds the parent reference")],
            }, SplitChild));
        }

        [Fact]
        public void ForeignKeysSharingAConstraintName_NeedOneRowEach()
        {
            void SecondDependent(ModelBuilder m) => m.Entity<Fixtures.Blue.Models.Other>().ToTable("Others")
                .HasOne<Fixtures.Parent>().WithMany().HasForeignKey(o => o.ParentId)
                .HasConstraintName("FK_Children_Parents_ParentId");
            var ledger = Ledger();

            Assert.Contains(Evaluate(ledger, SecondDependent),
                f => f.StartsWith("undeclared cross-owner foreign key FK_Children_Parents_ParentId on Others"));
            Assert.Empty(Evaluate(ledger with
            {
                ForeignKeys = [.. ledger.ForeignKeys, new("Others", "FK_Children_Parents_ParentId", "Blue", "Red", "same name, second table")],
            }, SecondDependent));
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
        public void DistinctOwnedTable_IsOwnedByTheOwnedTypesNamespace() =>
            Assert.Equal("Blue", Scan(Ledger(), m => m.Entity<Fixtures.Parent>().OwnsOne(p => p.Value,
                owned => owned.ToTable("OwnedValues"))).Owners["OwnedValues"]);

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
            Assert.Equal("Red", Scan(Ledger(), m => m.Entity<Fixtures.Parent>().ToTable("Parents", "farm")).Owners["farm.Parents"]);

        [Fact]
        public void PublicSchema_UsesUnqualifiedTableName() =>
            Assert.Empty(Evaluate(Ledger(), m => m.Entity<Fixtures.Parent>().ToTable("Parents", "public")));

        [Fact]
        public void ViewOnlyEntity_IsNotATable() =>
            Assert.Empty(Evaluate(Ledger(), m => m.Entity<Fixtures.ViewRow>().HasNoKey().ToView("ViewRows")));

        // Validate's own row checks. The scanner checks some of the same rows, so each assertion names
        // Validate's message.
        private static void AssertValidateRejects(ModuleLedger ledger, string expected)
        {
            var validated = ModuleLedger.Validate(ledger);
            Assert.Contains(expected, validated.RegistryErrors);
            Assert.Contains($"table-owner registry error: {expected}", Evaluate(validated));
        }

        [Fact]
        public void BlankForeignKeyName_IsRejectedByValidate() => AssertValidateRejects(
            Ledger() with { ForeignKeys = [Ledger().ForeignKeys[0] with { Name = "" }] },
            "foreignKeys[0] has a blank or non-string 'name'");

        [Fact]
        public void BlankForeignKeyReason_IsRejectedByValidate() => AssertValidateRejects(
            Ledger() with { ForeignKeys = [Ledger().ForeignKeys[0] with { Reason = " " }] },
            "foreignKeys[0] has a blank or non-string 'reason'");

        [Fact]
        public void BlankOverrideReason_IsRejectedByValidate() => AssertValidateRejects(
            Ledger() with { TableOwnerOverrides = [new("Parents", "Blue", "")] },
            "tableOwnerOverrides[0] has a blank or non-string 'reason'");

        [Fact]
        public void BlankOverrideOwner_IsRejectedByValidate() => AssertValidateRejects(
            Ledger() with { TableOwnerOverrides = [new("Parents", " ", "fixture")] },
            "tableOwnerOverrides[0] has a blank or non-string 'owner'");

        [Fact]
        public void DuplicateForeignKey_IsRegistryError() =>
            Assert.Contains("table-owner registry error: duplicate foreign-key row 'FK_Children_Parents_ParentId'",
                Evaluate(Ledger() with { ForeignKeys = [.. Ledger().ForeignKeys, Ledger().ForeignKeys[0]] }));

        [Fact]
        public void BlankOverrideReason_IsRegistryError() =>
            Assert.Contains("table-owner registry error: table-owner override 'Children' has a blank reason",
                Evaluate(Ledger() with { TableOwnerOverrides = [new("Children", "Red", " ")] }));

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

        private sealed class FixtureContext(Action<ModelBuilder>? configure) : DbContext
        {
            protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) => optionsBuilder
                .UseNpgsql("Host=localhost;Database=unreachable;Username=unreachable;Password=unreachable")
                // EF validates FKs against the primary table even when a fragment holds the columns.
                .ConfigureWarnings(w => w.Log(RelationalEventId.ForeignKeyPropertiesMappedToUnrelatedTables))
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
        public string Name { get; set; } = "";
        public string Note { get; set; } = "";
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
        public string Name { get; set; } = "";
    }

    public sealed class Other
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
