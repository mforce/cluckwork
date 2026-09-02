namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.Text.Json;
using Cluckwork.Api.Endpoints.Auth;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Domain.Flocks;
using Cluckwork.Domain.Sales;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

// #512 US1 — discovery over the EXISTING list routes: literal case-insensitive
// name search, stable `Name, Id` paging, and explicit flock eligibility applied
// BEFORE ordering and paging, with the compatibility rules that keep every
// legacy caller (`/flocks`, `?includeArchived=true`, `?limit=500`,
// `/customers`) on exactly its current behaviour. The contract is
// specs/001-searchable-entity-picker/contracts/http-api.md, whose evaluation
// order — tenant/flock-scope filter → eligibility → search → ORDER BY Name, Id
// → offset/limit — is what the paging and eligibility tests are the only proof
// of.
//
// How these assertions stay honest against the database this collection shares:
//
//   * Fixture names carry a per-test token and rows are matched by ID, so no
//     other test's row can satisfy or mask an expectation, and no expectation
//     is a bare global row count.
//   * Every expectation about ORDER is a LITERAL name, a hardcoded count, or a
//     property of the returned window (disjoint, complete, tie group
//     contiguous + Id-ascending, head-of-page). None of them re-runs the
//     reduction the server is contracted to perform: an expectation built by
//     calling OrderBy(Name).ThenBy(Id) on the fixture would stay green when the
//     server does not order at all, which is the defect class #512 is about.
//   * Search-semantics cases are each a POSITIVE row (its name contains the
//     query verbatim) paired with a NEGATIVE row that only the WILDCARD reading
//     would match, so the assertion names both.
[Collection(IntegrationCollection.Name)]
public sealed class NamedEntityDiscoveryTests(CluckworkWebApplicationFactory factory)
{
    // --- fixture ---------------------------------------------------------------

    // 121 flocks: 20 groups of six, and every group's six rows share ONE name —
    // 2 Active + 2 Depleted + 2 Archived. Duplicate names are a supported state
    // (spec US1 scenario 2): `FlockConfiguration` indexes only
    // (AccountId, FarmId, HouseId) and `Flock.Create` guards length alone, so the
    // model has no name uniqueness whatsoever and every group here is a tie the
    // Id tie-break has to break. Plus one Archived-only row.
    //
    // Derived fixture facts, each quoted as a literal below: eligible = 20 × 4 =
    // 80; all statuses = 80 + 40 + 1 = 121; Active-only = 40. Each group
    // contributes exactly 4 eligible rows, so rank 51 — the head of
    // `offset=50` — falls inside grp13's tie, after 12 × 4 = 48 `grpNN` rows.
    //
    // Duplicate CUSTOMER names are NOT seeded here: with the whole collection
    // sharing one database, another test's `Customer.Create("Seed Customer", …)`
    // could collide with any fixed name this fixture picked, which would make a
    // global page-1 length a coin flip. Customer duplicates live in
    // DuplicateNamePair_* below, on a tenant with no other content.
    private sealed class Fixture
    {
        public required string Marker { get; init; }
        public required List<Guid> ActiveIds { get; init; }
        public required List<Guid> DepletedIds { get; init; }
        public required List<Guid> ArchivedIds { get; init; }
        public required Guid LoneArchivedId { get; init; }
        public required List<Guid> CustomerIds { get; init; }

        public IEnumerable<Guid> AllFlockIds =>
            ActiveIds.Concat(DepletedIds).Concat(ArchivedIds).Append(LoneArchivedId);
        public HashSet<Guid> FlockIdSet => AllFlockIds.ToHashSet();
        // 20 groups × (2 Active + 2 Depleted) = 80. Every group is a tie: its
        // four eligible rows carry one name, so the 50-row window's boundary at
        // rank 51 lands inside grp13's tie, not between two names.
        public int EligibleCount => ActiveIds.Count + DepletedIds.Count;
        public int AllCount => EligibleCount + ArchivedIds.Count + 1;
    }

    private async Task<(HttpClient Client, Fixture Fixture)> SeedAsync()
    {
        var email = $"f-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        return (client, await SeedFixtureAsync(accountId));
    }

    private async Task<Fixture> SeedFixtureAsync(Guid accountId)
    {
        var marker = "fx" + Guid.NewGuid().ToString("N");
        var active = new List<Guid>();
        var depleted = new List<Guid>();
        var archived = new List<Guid>();
        var loneArchived = Guid.NewGuid();
        var customers = new List<Guid>();

        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            for (var g = 1; g <= 20; g++)
            {
                var name = $"{marker} grp{g:D2}";
                for (var tier = 0; tier < 6; tier++)
                {
                    var id = Guid.NewGuid();
                    var status = tier switch
                    {
                        < 2 => FlockStatus.Active,
                        < 4 => FlockStatus.Depleted,
                        _ => FlockStatus.Archived,
                    };
                    (status switch
                    {
                        FlockStatus.Active => active,
                        FlockStatus.Depleted => depleted,
                        _ => archived,
                    }).Add(id);
                    db.Flocks.Add(Build(id, name, status));
                }
            }

            db.Flocks.Add(Build(loneArchived, $"{marker} lone archived", FlockStatus.Archived));

            for (var c = 1; c <= 101; c++)
            {
                var id = Guid.NewGuid();
                db.Customers.Add(Customer.Create(id, accountId, $"{marker}-c{c:D3}", "555-0000"));
                customers.Add(id);
            }

            await db.SaveChangesAsync();
        });

        return new Fixture
        {
            Marker = marker, ActiveIds = active, DepletedIds = depleted, ArchivedIds = archived,
            LoneArchivedId = loneArchived, CustomerIds = customers,
        };

        Flock Build(Guid id, string name, FlockStatus status)
        {
            var flock = Flock.Create(id, accountId, Guid.NewGuid(), Guid.NewGuid(),
                name, "Discovery Breed", new DateOnly(2026, 1, 1), 100);
            if (status is FlockStatus.Depleted or FlockStatus.Archived)
                flock.Deplete(new DateOnly(2026, 2, 1));
            if (status == FlockStatus.Archived)
                flock.Archive(new DateOnly(2026, 3, 1));
            return flock;
        }
    }

    // --- rows -----------------------------------------------------------------

    private sealed record FlockRow(
        Guid Id, Guid FarmId, Guid HouseId, string Name, string Breed,
        DateOnly PlacementDate, int InitialCount, long CurrentBirds, string Status);

    private sealed record CustomerRow(
        Guid Id, string Name, string Phone, string? Email, string? Address,
        string? Note, int Version);

    private sealed record ValidationProblem(
        string? Title, string? Detail, int Status,
        Dictionary<string, string[]> Errors,
        Dictionary<string, string?[]>? ErrorCodes);

    private sealed record UserRow(Guid Id, string Email, string? DisplayName, string Role);

    private static readonly JsonSerializerOptions ProblemJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static string Q(string value) => Uri.EscapeDataString(value);

    // The names the literal-search theory is run against — AND the rows it
    // seeds, from this one list, so a theory case can never name a tail the
    // fixture did not seed. Each entry is built so the WILDCARD reading of its
    // pair's query reaches it: "pct-off" is what `%` matches between "pct" and
    // "off", "50x" is what `%x` matches, "axb" is what `_` matches, and
    // "backslash" is what a literal backslash collapses to.
    private static readonly string[] LiteralTails =
    [
        "pct%off", "pct-off", "50%x", "50x", "a_b", "axb", "back\\slash", "backslash", "tail\\",
    ];

    private async Task<List<FlockRow>> GetFlocksAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync("/api/v1/flocks" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = await response.Content.ReadFromJsonAsync<List<FlockRow>>();
        Assert.NotNull(rows);
        return rows;
    }

    private async Task<List<CustomerRow>> GetCustomersAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync("/api/v1/customers" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = await response.Content.ReadFromJsonAsync<List<CustomerRow>>();
        Assert.NotNull(rows);
        return rows;
    }

    // The fixture's own rows inside one response. Every flock read in this file
    // is a search or a window whose counts the fixture pins, so no flock case
    // ever reads the whole catalogue — a completeness parameter here would be a
    // branch nothing takes.
    private static List<FlockRow> Mine(List<FlockRow> rows, Fixture f)
    {
        var owned = f.FlockIdSet;
        return rows.Where(r => owned.Contains(r.Id)).ToList();
    }

    // `expectFull` pins that a whole-catalogue read came back complete; a
    // search result is a subset and only ever filtered by ownership.
    private static List<CustomerRow> Mine(List<CustomerRow> rows, Fixture f, bool expectFull = false)
    {
        var owned = f.CustomerIds.ToHashSet();
        if (expectFull)
            Assert.Equal(owned.Count, rows.Count);
        return rows.Where(c => owned.Contains(c.Id)).ToList();
    }

    private static string SwapCase(string value) =>
        string.Concat(value.Select(c => char.IsUpper(c) ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c)));

    private static async Task<ValidationProblem> ReadProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblem>(ProblemJson);
        Assert.NotNull(problem);
        return problem;
    }

    // --- blank / trimmed / case-insensitive search (FR-002, FR-003) ------------

    // Blank search is an UNFILTERED search. Read as a `%%` pattern it would add
    // the Archived rows (41 more); read as "match nothing" it would return
    // none. Both wrong answers move these counts, and the counts are fixtures
    // facts rather than a reduction the endpoint itself produced.
    [Theory]
    [InlineData("")]                              // parameter absent
    [InlineData("?search=")]                      // present, empty
    [InlineData("?search=%20")]                   // one space
    [InlineData("?search=%20%09%20")]             // a whitespace run
    [InlineData("?search=&limit=500&offset=0")]   // blank alongside paging
    public async Task BlankSearch_ReturnsUnfilteredEligibleRows(string query)
    {
        var (client, f) = await SeedAsync();

        // `&limit=500` because the route's own default is 100 rows and the
        // fixture is 80 flocks + 101 customers; a page shorter than the fixture
        // would make "unfiltered returned everything" unobservable.
        var suffix = query.Contains("limit") ? "" : "&limit=500";
        var full = query.Length == 0 ? "?limit=500" : query + suffix;
        var flocks = Mine(await GetFlocksAsync(client, full), f);
        Assert.Equal(80, flocks.Count);
        Assert.Equal(f.EligibleCount, flocks.Count);   // same fact, from the fixture
        Assert.DoesNotContain(flocks, r => r.Status == "Archived");

        Assert.Equal(101, Mine(
            await GetCustomersAsync(client, full), f, expectFull: true).Count);
    }

    // FR-002 — the query's case never matters: lower, UPPER and Mixed return
    // the same rows, including when the case sits inside the token itself.
    [Theory]
    [InlineData("grp01")]
    [InlineData("GRP01")]
    [InlineData("gRp01")]
    public async Task Search_IsCaseInsensitive(string tail)
    {
        var (client, f) = await SeedAsync();

        var anyCase = Mine(await GetFlocksAsync(client, $"?search={Q(tail)}&limit=500"), f);
        var upper = Mine(await GetFlocksAsync(client, $"?search={Q(tail.ToUpperInvariant())}&limit=500"), f);
        var swapped = Mine(await GetFlocksAsync(client, $"?search={Q(SwapCase(tail))}&limit=500"), f);

        // One tie group is 6 rows: 2 Active, 2 Depleted, 2 Archived. The default
        // policy hides the archived pair, so a case-insensitive match is 4 rows
        // whichever case the query arrived in.
        Assert.Equal(4, anyCase.Count);
        Assert.Equal(anyCase.Select(r => r.Id).OrderBy(id => id), upper.Select(r => r.Id).OrderBy(id => id));
        Assert.Equal(anyCase.Select(r => r.Id).OrderBy(id => id), swapped.Select(r => r.Id).OrderBy(id => id));
        Assert.All(anyCase, r => Assert.EndsWith("grp01", r.Name, StringComparison.Ordinal));
    }

    // A match that spans the token boundary, with the token upper-cased and the
    // tail lower-cased in the same query — the shape a folded `ToLower()` on
    // only one side would miss.
    [Fact]
    public async Task Search_IsCaseInsensitive_AcrossTheQueryAndStoredName()
    {
        var (client, f) = await SeedAsync();

        // Uppercase token, lowercase group suffix, in one query: a `ToLower()`
        // applied to only one side of the comparison misses this entirely.
        var rows = Mine(await GetFlocksAsync(client,
            $"?search={Q(f.Marker.ToUpperInvariant() + " grp04")}&limit=500"), f);
        Assert.Equal(4, rows.Count);
        Assert.All(rows, r => Assert.EndsWith("grp04", r.Name, StringComparison.Ordinal));
    }

    // FR-003 — leading AND trailing whitespace, tab and newline included, is
    // trimmed once. An untrimmed search matches nothing here; a trim at one end
    // only matches nothing for the other half of the padding.
    [Fact]
    public async Task Search_IsTrimmed_AtBothEnds()
    {
        var (client, f) = await SeedAsync();

        // grp13 is where the page boundary falls, and the search is padded so an
        // untrimmed query would match nothing: the padded and tight forms must
        // agree on the whole result, not merely both return "something".
        var tight = Mine(await GetFlocksAsync(client,
            $"?search={Q(f.Marker + " grp13")}&limit=500"), f);
        var padded = Mine(await GetFlocksAsync(client,
            $"?search={Q("  \t" + f.Marker + " grp13\n ")}&limit=500"), f);
        Assert.Equal(4, tight.Count);   // grp13's two Active + two Depleted rows
        Assert.Equal(tight.Select(r => r.Id).OrderBy(id => id), padded.Select(r => r.Id).OrderBy(id => id));

        // The same rule for customers, where `search` is the only new parameter.
        var customer = Mine(await GetCustomersAsync(client,
            $"?search={Q("   " + f.Marker + "-c007" + "  ")}"), f);
        Assert.Single(customer);
        Assert.Equal($"{f.Marker}-c007", customer[0].Name);
    }

    // FR-004 — `%`, `_` and the escape character are ordinary characters. Each
    // case pairs a row whose name contains the query VERBATIM with a row only
    // the wildcard reading would match, so an unescaped Contains/ILike — or an
    // escape applied after the surrounding wildcards — cannot pass.
    [Theory]
    // "%" as any-run would match "pct-off" too.
    [InlineData("pct%off", "pct%off", "pct-off")]
    // "50%x" as a pattern matches "50x"; as a literal it matches nothing.
    [InlineData("50%x", null, "50x")]
    // "_" as one-char-any matches both "a_b" and "axb".
    [InlineData("a_b", "a_b", "axb")]
    // The escape character is data: literal "\slash" matches "back\slash" only,
    // never "backslash".
    [InlineData("back\\slash", "back\\slash", "backslash")]
    // A trailing escape character is data, not a syntax error.
    [InlineData("tail\\", "tail\\", null)]
    public async Task Search_MatchesWildcardsAndEscapeCharacterLiterally(
        string query, string? expectedTail, string? forbiddenTail)
    {
        var email = $"lit-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        var marker = "lit" + Guid.NewGuid().ToString("N");

        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            foreach (var tail in LiteralTails)
            {
                var id = Guid.NewGuid();
                db.Flocks.Add(Flock.Create(id, accountId, Guid.NewGuid(), Guid.NewGuid(),
                    $"{marker} {tail}", "Breed", new DateOnly(2026, 1, 1), 100));
            }
            await db.SaveChangesAsync();
        });

        var rows = await GetFlocksAsync(client,
            $"?search={Q(query)}&limit=500");

        // Only this test's rows can answer — the query is unique to them — and
        // each answer must contain the query VERBATIM, escape character and all.
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.StartsWith(marker, r.Name, StringComparison.Ordinal));
        Assert.All(rows, r => Assert.Contains(query, r.Name, StringComparison.Ordinal));
        if (expectedTail is not null)
            Assert.Contains(rows, r => r.Name.EndsWith(expectedTail, StringComparison.Ordinal));
        if (forbiddenTail is not null)
            Assert.DoesNotContain(rows, r => r.Name.EndsWith(forbiddenTail, StringComparison.Ordinal));
    }

    // A lone `%` is data: it matches only rows whose names literally carry it,
    // never the whole catalogue.
    [Fact]
    public async Task Search_LonePercentSignIsNotAWildcard()
    {
        var email = $"pc-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        var marker = "pc" + Guid.NewGuid().ToString("N");

        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            db.Flocks.Add(Flock.Create(Guid.NewGuid(), accountId, Guid.NewGuid(), Guid.NewGuid(),
                $"{marker} 50%off", "Breed", new DateOnly(2026, 1, 1), 100));
            db.Flocks.Add(Flock.Create(Guid.NewGuid(), accountId, Guid.NewGuid(), Guid.NewGuid(),
                $"{marker} 50off", "Breed", new DateOnly(2026, 1, 1), 100));
            await db.SaveChangesAsync();
        });

        // "50%" as a pattern would match "50off" too; as a literal it can only
        // ever match the row whose name contains "50%".
        var rows = await GetFlocksAsync(client, $"?search={Q("50%")}&limit=500");
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.StartsWith(marker, r.Name, StringComparison.Ordinal));
        Assert.All(rows, r => Assert.Contains("50%", r.Name, StringComparison.Ordinal));
        Assert.DoesNotContain(rows, r => r.Name == $"{marker} 50off");
    }

    // --- stable paging (FR-006, FR-007) ---------------------------------------

    // The page-1/page-2 walk over a fixture with 15 name-tie groups and five
    // duplicates. Disjoint, complete, and every tie group arrives contiguous
    // and Id-ascending — the three properties a name-only ORDER BY breaks, and
    // none of them is a re-run of the server's sort.
    [Fact]
    public async Task Paging_WalkIsDisjointCompleteAndTieBrokenById()
    {
        var (client, f) = await SeedAsync();

        // 60+60 covers all 80 eligible rows, so "complete" is a real assertion;
        // two 50-row pages would leave the tail outside the walk entirely. The
        // boundary is at rank 60, inside grp16's tie, so "contiguous" below is
        // tested against a tie the walk actually cuts through.
        var page1 = await GetFlocksAsync(client, "?limit=60&offset=0");
        var page2 = await GetFlocksAsync(client, "?limit=60&offset=60");
        Assert.Equal(60, page1.Count);
        Assert.Equal(20, page2.Count);
        Assert.DoesNotContain(page1.Concat(page2), r => !f.FlockIdSet.Contains(r.Id));

        // Disjoint — a tie served in both windows is the first thing a missing
        // Id tie-break produces.
        Assert.Empty(page1.Select(r => r.Id).Intersect(page2.Select(r => r.Id)));

        var walked = page1.Concat(page2).ToList();
        Assert.Equal(f.EligibleCount, walked.Count);   // complete: nothing skipped
        Assert.Equal(walked.Select(r => r.Id).Distinct().Count(), walked.Count);

        foreach (var group in walked.GroupBy(r => r.Name).Where(g => g.Count() > 1))
        {
            // Contiguous: a tie group split across the page boundary would show
            // up as two runs of the same name.
            var positions = walked.Select((r, i) => (r.Name, i)).Where(x => x.Name == group.Key)
                .Select(x => x.i).ToList();
            Assert.Equal(Enumerable.Range(positions[0], positions.Count), positions);
            Assert.Equal(group.Select(r => r.Id).OrderBy(id => id), group.Select(r => r.Id));
        }
    }

    // FR-007 + the scale requirement, stated as literals: the first 12 groups'
    // eligible rows come before the 13th's, so `offset=50` opens INSIDE grp13's
    // name tie. The group names are zero-padded (`grp01`…`grp20`) so the group
    // order holds under any collation, and "12 groups of 4, then the 13th" is
    // arithmetic on 4-per-group — not a sort this test performs itself.
    [Fact]
    public async Task Paging_LateSortingRows_StartTheSecondPage()
    {
        var (client, f) = await SeedAsync();

        var page1 = await GetFlocksAsync(client, "?limit=50&offset=0");
        var page2 = await GetFlocksAsync(client, "?limit=50&offset=50");
        Assert.Equal(50, page1.Count);

        // Groups sort grp01 < grp02 < … < grp20 and each contributes exactly 4
        // eligible rows, so page 1 (ranks 1–50) ends INSIDE grp13 (ranks 49–52)
        // and page 2 starts there. That split is the point: it is only reachable
        // when the ordering is right, because grp13's four rows share a name and
        // only the Id tie-break decides which two of them land on page 1.
        var thirteenth = $"{f.Marker} grp13";
        var twelfth = $"{f.Marker} grp12";
        Assert.Contains(page1, r => r.Name == thirteenth);
        Assert.DoesNotContain(page2, r => r.Name == twelfth);
        Assert.Equal([thirteenth, thirteenth], [page2[0].Name, page2[1].Name]);
        Assert.Contains(page1, r => r.Name == twelfth);

        // The two grp13 rows the boundary splits are ordered by Id on BOTH sides
        // of it: every page-1 tail Id precedes every page-2 head Id.
        Assert.True(page2[0].Id < page2[1].Id);
        var page1Tail = page1.Where(r => r.Name == thirteenth).Select(r => r.Id).ToList();
        Assert.Equal(2, page1Tail.Count);
        Assert.All(page1Tail, id => Assert.True(id < page2[0].Id));
    }

    // Beyond the end is an empty page, not a repeat of the last one — the
    // property behind the picker's "one final empty request ends paging" rule.
    [Fact]
    public async Task Paging_BeyondTheEnd_ReturnsAnEmptyPage()
    {
        var (client, f) = await SeedAsync();

        var first = Mine(await GetFlocksAsync(client, "?limit=50&offset=0"), f);
        var beyond = Mine(await GetFlocksAsync(client, "?limit=50&offset=1000"), f);

        Assert.NotEmpty(first);
        Assert.Empty(beyond);
    }

    // The same contract for customers, whose only new parameter is `search`.
    [Fact]
    public async Task Paging_CustomerDiscovery_IsStableAcrossPages()
    {
        var (client, f) = await SeedAsync();
        var owned = f.CustomerIds.ToHashSet();

        var page1 = await GetCustomersAsync(client, "?limit=50&offset=0");
        var page2 = await GetCustomersAsync(client, "?limit=50&offset=50");
        var page3 = await GetCustomersAsync(client, "?limit=50&offset=100");
        var page4 = await GetCustomersAsync(client, "?limit=50&offset=150");

        Assert.All(page1.Concat(page2).Concat(page3), c => Assert.Contains(c.Id, owned));
        Assert.Empty(page1.Select(c => c.Id).Intersect(page2.Select(c => c.Id)));
        Assert.Empty(page2.Select(c => c.Id).Intersect(page3.Select(c => c.Id)));
        Assert.Equal(owned, page1.Concat(page2).Concat(page3).Select(c => c.Id).ToHashSet());
        Assert.Empty(page4);

        // The walk is exactly this tenant's customers, once each. Deliberately
        // NOT an Ordinal re-sort: the server orders by the database collation,
        // not by Ordinal, so a collation-dependent expectation is a second sort
        // this test performs rather than the contract. Completeness and
        // disjointness above, plus the boundary repeatability below, are the
        // order facts that hold whatever the collation does.
        var walked = page1.Concat(page2).Concat(page3).Select(c => c.Id).ToList();
        Assert.Equal(owned.Count, walked.Count);
        Assert.Equal(owned.Count, walked.Distinct().Count());
    }

    // `Id` as the tie-break must make one request repeatable, and overlapping
    // windows agree: without it, the same request can disagree with itself and
    // a shifted window can lose or repeat a row.
    [Fact]
    public async Task Paging_TheSameRequestRepeatsAndShiftedWindowsAgree()
    {
        var (client, _) = await SeedAsync();

        // Repeatability is the whole point of the Id tie-break: two identical
        // requests must agree exactly, or a duplicate row can land in one
        // window and not the next.
        var head = await GetFlocksAsync(client, "?limit=50&offset=0");
        var again = await GetFlocksAsync(client, "?limit=50&offset=0");
        Assert.Equal(head.Select(r => r.Id), again.Select(r => r.Id));

        // And overlapping windows must agree on their overlap — the property a
        // page boundary loses a row or serves it twice would break.
        var shifted = await GetFlocksAsync(client, "?limit=49&offset=1");
        Assert.Equal(head.Skip(1).Select(r => r.Id), shifted.Select(r => r.Id));
        var deeper = await GetFlocksAsync(client, "?limit=48&offset=2");
        Assert.Equal(shifted.Skip(1).Select(r => r.Id), deeper.Select(r => r.Id));
    }

    // Duplicate names are the case that cannot be simulated inside a shared
    // tenant: in this collection another test's `Customer.Create("Seed
    // Customer", …)` can collide with any fixed name, which would make a global
    // page-1 length a coin flip. So this runs on a tenant with nothing else in
    // it, where the whole catalogue is two rows and the outcome is forced —
    // exactly one of them on each page, never both on one and neither on the
    // other. That is the property a missing `ThenBy(Id)` breaks, and a fixture
    // where the duplicates are far apart cannot show it.
    [Fact]
    public async Task DuplicateNamePair_SitsOnePerPageAcrossTheBoundary()
    {
        var email = $"d-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        // A–E are four DISTINCT names that are certain to sort before the
        // duplicate under any collation, so the tie group is forced to positions
        // 5–6 of a walk read 5 at a time: the offset=5 window opens exactly on
        // the SECOND duplicate. A boundary that falls inside a tie group is the
        // only place where "a page lost or repeated a duplicate" is observable.
        var dupName = "zzz shared duplicate name";
        var ids = new List<Guid>();

        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            foreach (var name in new[] { "AAA", "BBB", "CCC", "DDD", "EEE" })
            {
                var id = Guid.NewGuid();
                ids.Add(id);
                db.Customers.Add(Customer.Create(id, accountId, name, "555-0000"));
            }
            for (var d = 0; d < 2; d++)
            {
                var id = Guid.NewGuid();
                ids.Add(id);
                db.Customers.Add(Customer.Create(id, accountId, dupName, "555-0000"));
            }
            await db.SaveChangesAsync();
        });

        // Catalogue: AAA…EEE then the duplicate twice. Read 5 at a time, page 1
        // is the five distinct names and page 2 is the tie group.
        var page1 = await client.GetFromJsonAsync<List<CustomerRow>>("/api/v1/customers?limit=5&offset=0");
        var page2 = await client.GetFromJsonAsync<List<CustomerRow>>("/api/v1/customers?limit=5&offset=5");
        Assert.NotNull(page1);
        Assert.NotNull(page2);

        Assert.Equal(5, page1.Count);
        Assert.Equal(2, page2.Count);
        Assert.DoesNotContain(page2, c => !ids.Contains(c.Id));   // no foreign tenant's rows leak in
        Assert.Empty(page1.Select(c => c.Id).Intersect(page2.Select(c => c.Id)));
        Assert.Equal(ids.OrderBy(id => id),
            page1.Concat(page2).Select(c => c.Id).OrderBy(id => id));

        // The tie group is adjacent in the walk and entirely on page 2; the
        // completeness assertion above is what catches a lost or repeated row,
        // and the Id order within the group is what makes the split stable.
        var walk = page1.Concat(page2).ToList();
        var dupPositions = walk.Select((c, i) => (c.Name, i)).Where(x => x.Name == dupName)
            .Select(x => x.i).ToList();
        Assert.Equal([5, 6], dupPositions);
        Assert.Equal(
            walk.Where(c => c.Name == dupName).Select(c => c.Id).OrderBy(id => id).ToList(),
            walk.Where(c => c.Name == dupName).Select(c => c.Id).ToList());

        // Repeating the request must return the same split — the tie-break is
        // what makes it deterministic, not the query plan's luck.
        var page1Again = await client.GetFromJsonAsync<List<CustomerRow>>("/api/v1/customers?limit=5&offset=0");
        Assert.NotNull(page1Again);
        Assert.Equal(page1.Select(c => c.Id), page1Again.Select(c => c.Id));
    }

    // --- eligibility (FR-005, FR-009, FR-010) ---------------------------------

    // Each policy is an exact set — count, status mix and IDs. `active` must
    // exclude the Depleted rows, which is the property the legacy boolean
    // cannot express, and `all` must include Archived, which the default hides.
    [Theory]
    [InlineData("active", 40, 40, 0, 0)]
    [InlineData("active-and-depleted", 80, 40, 40, 0)]
    [InlineData("all", 121, 40, 40, 41)]
    public async Task Eligibility_ReturnsExactlyItsStatusSet(
        string eligibility, int total, int active, int depleted, int archived)
    {
        var (client, f) = await SeedAsync();

        var rows = Mine(await GetFlocksAsync(client, $"?eligibility={eligibility}&limit=500&offset=0"), f);

        Assert.Equal(total, rows.Count);
        Assert.Equal(active, rows.Count(r => r.Status == "Active"));
        Assert.Equal(depleted, rows.Count(r => r.Status == "Depleted"));
        Assert.Equal(archived, rows.Count(r => r.Status == "Archived"));

        var expected = eligibility switch
        {
            "active" => f.ActiveIds,
            "active-and-depleted" => f.ActiveIds.Concat(f.DepletedIds),
            _ => f.AllFlockIds,
        };
        Assert.Equal(expected.OrderBy(id => id), rows.Select(r => r.Id).OrderBy(id => id));
    }

    // FR-010 — omission means Active+Depleted: identical to that explicit
    // policy, and not identical to `all`.
    [Fact]
    public async Task Eligibility_Omitted_IsActiveAndDepleted()
    {
        var (client, f) = await SeedAsync();

        var omitted = Mine(await GetFlocksAsync(client, "?limit=500&offset=0"), f);
        var explicitPair = Mine(
            await GetFlocksAsync(client, "?eligibility=active-and-depleted&limit=500&offset=0"), f);
        var all = Mine(await GetFlocksAsync(client, "?eligibility=all&limit=500&offset=0"), f);

        Assert.Equal(explicitPair.Select(r => r.Id).OrderBy(id => id), omitted.Select(r => r.Id).OrderBy(id => id));
        Assert.Equal(80, omitted.Count);
        Assert.Equal(121, all.Count);
        Assert.DoesNotContain(omitted, r => r.Status == "Archived");
        Assert.Contains(all, r => r.Id == f.LoneArchivedId);
    }

    // FR-005 — eligibility runs BEFORE paging, and a post-paging filter cannot
    // fake it: `active` selects only every 1st, 2nd, 7th, 8th, 13th… name, so
    // the archived pair that sits between them must NOT appear, while the
    // window still has to be full. Filtering after paging returns short pages or
    // the same rows the wider policy returns.
    [Fact]
    public async Task Eligibility_IsAppliedBeforePaging()
    {
        var (client, f) = await SeedAsync();

        var narrow = Mine(await GetFlocksAsync(client,
            $"?search={Q(f.Marker)}&eligibility=active&limit=10&offset=0"), f);
        Assert.Equal(10, narrow.Count);            // full page, not 10-minus-archived
        Assert.All(narrow, r => Assert.Equal("Active", r.Status));

        // The window is the head of the same policy unpaginated…
        var narrowAll = Mine(await GetFlocksAsync(client,
            $"?search={Q(f.Marker)}&eligibility=active&limit=50&offset=0"), f);
        Assert.Equal(40, narrowAll.Count);
        Assert.Equal(narrowAll.Take(10).Select(r => r.Id), narrow.Select(r => r.Id));

        // …and a different head under the wider policy at the same offset,
        // because the archived rows are candidates the narrow policy removed
        // before the window was cut.
        var wide = Mine(await GetFlocksAsync(client,
            $"?search={Q(f.Marker)}&eligibility=all&limit=10&offset=0"), f);
        Assert.Equal(10, wide.Count);
        Assert.NotEqual(narrow.Select(r => r.Id), wide.Select(r => r.Id));
    }

    // The same rule for `search`: a full window, every row matching, and the
    // head of the same search unpaginated.
    [Fact]
    public async Task Search_IsAppliedBeforePaging()
    {
        var (client, f) = await SeedAsync();

        // A search whose whole eligible result is one tie group: the window must
        // be that group's 4 eligible rows, so a window sliced before filtering
        // could not reproduce it.
        var groupWindow = Mine(await GetFlocksAsync(client,
            $"?search={Q(f.Marker + " grp07")}&limit=10&offset=0"), f);
        Assert.Equal(4, groupWindow.Count);
        Assert.All(groupWindow, r => Assert.EndsWith("grp07", r.Name, StringComparison.Ordinal));
        Assert.DoesNotContain(groupWindow, r => r.Status == "Archived");

        var window = Mine(await GetFlocksAsync(client,
            $"?search={Q(f.Marker)}&limit=10&offset=0"), f);
        Assert.Equal(10, window.Count);
        Assert.All(window, r => Assert.Contains(f.Marker, r.Name, StringComparison.Ordinal));
    }

    // --- invalid requests (FR-011) --------------------------------------------

    // Unknown eligibility values are rejected, and the wire is CASE-SENSITIVE:
    // a case or hyphen variant is "unknown", not a synonym. The three accepted
    // values are pinned by the behaviour tests above, so an over-permissive
    // parser fails THOSE rather than these.
    [Theory]
    [InlineData("ALL")]
    [InlineData("Active")]
    [InlineData("Active-And-Depleted")]
    [InlineData("ACTIVE-AND-DEPLETED")]
    [InlineData("Archived")]
    [InlineData("active,depleted")]
    [InlineData("active AND depleted")]
    [InlineData("ActiveAndDepleted")]
    [InlineData("all ")]
    [InlineData(" unknown")]
    [InlineData("active-and-depleted ")]
    public async Task Eligibility_UnknownValue_ReturnsValidationProblem400(string eligibility)
    {
        var (client, _) = await SeedAsync();

        var response = await client.GetAsync("/api/v1/flocks?eligibility=" + Q(eligibility));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal(400, problem.Status);
        Assert.Contains("eligibility", problem.Errors.Keys);
        Assert.NotEmpty(problem.Errors["eligibility"]);
    }

    // An empty or whitespace wire value is an unknown value too: "unspecified"
    // is expressed by omitting the parameter, not by sending an empty one.
    [Theory]
    [InlineData("")]
    [InlineData("%20")]
    public async Task Eligibility_EmptyValue_ReturnsValidationProblem400(string eligibility)
    {
        var (client, _) = await SeedAsync();

        var response = await client.GetAsync("/api/v1/flocks?eligibility=" + eligibility);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("eligibility", (await ReadProblemAsync(response)).Errors.Keys);
    }

    // FR-011 — both keys present is invalid EVEN when includeArchived=false.
    // A `bool includeArchived = false` parameter cannot tell absent from
    // explicit-false, which is exactly why the contract requires the legacy
    // parameter to bind as nullable.
    [Theory]
    [InlineData("?eligibility=all&includeArchived=false")]
    [InlineData("?eligibility=all&includeArchived=true")]
    [InlineData("?eligibility=active&includeArchived=false")]
    [InlineData("?eligibility=active-and-depleted&includeArchived=false")]
    [InlineData("?includeArchived=false&eligibility=all")]
    [InlineData("?eligibility=active&includeArchived=False")]
    public async Task Eligibility_WithLegacyParameter_ReturnsValidationProblem400(string query)
    {
        var (client, _) = await SeedAsync();

        var response = await client.GetAsync("/api/v1/flocks" + query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal(400, problem.Status);
        Assert.Contains("eligibility", problem.Errors.Keys);
    }

    // The rejection names the conflict instead of answering with data.
    [Fact]
    public async Task Eligibility_Conflict_NamesTheConflictingKeys()
    {
        var (client, _) = await SeedAsync();
        var problem = await ReadProblemAsync(
            await client.GetAsync("/api/v1/flocks?eligibility=all&includeArchived=false"));

        var namesBoth = problem.Errors.ContainsKey("eligibility")
            && (problem.Errors.ContainsKey("includeArchived")
                || problem.Errors.Values.SelectMany(v => v)
                    .Any(m => m.Contains("includeArchived", StringComparison.Ordinal)));
        Assert.True(namesBoth, "expected both query keys named; got "
            + string.Join("; ", problem.Errors.Select(kv => $"{kv.Key}: {string.Join(" | ", kv.Value)}")));
    }

    // --- legacy compatibility (FR-011, FR-012) --------------------------------

    [Fact]
    public async Task LegacyCallers_KeepTheirCurrentBehaviour()
    {
        var (client, f) = await SeedAsync();

        // `/flocks` — Active + Depleted, Archived excluded.
        var plain = Mine(await GetFlocksAsync(client, "?limit=500&offset=0"), f);
        Assert.Equal(80, plain.Count);
        Assert.DoesNotContain(plain, r => r.Status == "Archived");

        // `?includeArchived=true` — the legacy alias for `all`: everything.
        var legacy = Mine(await GetFlocksAsync(client, "?includeArchived=true&limit=500&offset=0"), f);
        Assert.Equal(121, legacy.Count);
        Assert.Equal(
            Mine(await GetFlocksAsync(client, "?eligibility=all&limit=500&offset=0"), f)
                .Select(r => r.Id).OrderBy(id => id),
            legacy.Select(r => r.Id).OrderBy(id => id));

        // `?includeArchived=false` ALONE stays legal and is the default.
        var legacyFalse = Mine(
            await GetFlocksAsync(client, "?includeArchived=false&limit=500&offset=0"), f);
        Assert.Equal(plain.Select(r => r.Id).OrderBy(id => id), legacyFalse.Select(r => r.Id).OrderBy(id => id));

        // Existing clamps: limit into 1..500, offset floored at 0.
        var clamped = await GetFlocksAsync(client, "?limit=9999&offset=-5");
        var bounded = await GetFlocksAsync(client, "?limit=500&offset=0");
        Assert.Equal(bounded.Select(r => r.Id), clamped.Select(r => r.Id));
        Assert.Single(await GetFlocksAsync(client, "?limit=0"));   // limit clamped up to 1

        // Bare-array responses unchanged: no envelope, no total, no new fields.
        foreach (var url in new[] { "/api/v1/flocks", "/api/v1/customers" })
        {
            using var doc = JsonDocument.Parse(await (await client.GetAsync(url)).Content.ReadAsStringAsync());
            Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                Assert.False(item.TryGetProperty("total", out _));
                Assert.False(item.TryGetProperty("items", out _));
            }
        }
    }

    // A caller that supplies `search` and nothing else keeps Active+Depleted:
    // search must not widen or narrow eligibility.
    [Fact]
    public async Task Search_WithoutEligibility_KeepsTheDefaultPolicy()
    {
        var (client, f) = await SeedAsync();

        var rows = await GetFlocksAsync(client, $"?search={Q(f.Marker)}&limit=500&offset=0");
        Assert.Equal(80, rows.Count);
        Assert.DoesNotContain(rows, r => r.Status == "Archived");
    }

    // --- tenant isolation ------------------------------------------------------

    // AGENTS.md multi-tenancy — `search` must not become a cross-tenant read.
    // Tenant B holds a row carrying A's token so a leak has somewhere to come
    // from, and B's own token search must return only B's row.
    [Fact]
    public async Task Search_NeverReturnsAnotherTenantsRows()
    {
        var (clientA, f) = await SeedAsync();

        var emailB = $"b-{Guid.NewGuid():N}@test.local";
        var accountIdB = await factory.SeedAccountWithUserAsync(emailB);
        var clientB = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(emailB));
        var stolenFlockId = Guid.NewGuid();
        var stolenCustomerId = Guid.NewGuid();
        await factory.WithTenantScopeAsync(accountIdB, async db =>
        {
            db.Flocks.Add(Flock.Create(stolenFlockId, accountIdB, Guid.NewGuid(), Guid.NewGuid(),
                "Stolen " + f.Marker, "Breed", new DateOnly(2026, 1, 1), 10));
            db.Customers.Add(Customer.Create(stolenCustomerId, accountIdB,
                "Stolen " + f.Marker, "555-0999"));
            await db.SaveChangesAsync();
        });

        var flocksB = await clientB.GetFromJsonAsync<List<FlockRow>>(
            $"/api/v1/flocks?search={Q(f.Marker)}&limit=500");
        Assert.NotNull(flocksB);
        Assert.Equal([stolenFlockId], flocksB.Select(r => r.Id));

        var customersB = await clientB.GetFromJsonAsync<List<CustomerRow>>(
            $"/api/v1/customers?search={Q(f.Marker)}&limit=500");
        Assert.NotNull(customersB);
        Assert.Equal([stolenCustomerId], customersB.Select(c => c.Id));

        // …and A never sees B's row, under any policy.
        foreach (var extra in new[] { "", "&eligibility=all", "&includeArchived=true" })
        {
            var rows = Mine(await GetFlocksAsync(clientA,
                $"?search={Q(f.Marker)}{extra}&limit=500&offset=0"), f);
            Assert.NotEmpty(rows);
            Assert.DoesNotContain(rows, r => r.Id == stolenFlockId);
        }
    }

    // --- Worker flock scope (#613) --------------------------------------------

    // The structural `AccountId AND flock-scope` filter must survive the new
    // predicates. Each query shape is checked for BOTH identities: the scoped
    // Worker gets only its assigned flock, and the Owner control on the same
    // query proves the unassigned flocks are reachable — so a Worker's empty
    // result means filtering, not a seed failure.
    [Fact]
    public async Task Discovery_RespectsFlockScope_ForEveryQueryShape()
    {
        var email = $"so-{Guid.NewGuid():N}@test.local";
        // /users is OwnerOnly, and the assignment write additionally needs a
        // step-up token minted from this account's own password — so the
        // assigning identity is an Owner, exactly as FlockScopeTests does it.
        var accountId = await factory.SeedAccountWithUserAsync(email, asAdmin: true);
        var owner = factory.CreateAuthedClient(
            await factory.LoginForAccessTokenAsync(email));
        var marker = "sc" + Guid.NewGuid().ToString("N");
        var (flockA, flockB, archivedB) = await SeedScopedFlocksAsync(accountId, marker);

        var workerEmail = $"sw-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, workerEmail, (string?)null);
        var worker = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(workerEmail));
        var workerId = (await owner.GetFromJsonAsync<List<UserRow>>("/api/v1/users"))!
            .Single(u => u.Email == workerEmail).Id;
        Assert.Equal(HttpStatusCode.Created,
            (await AssignFlockAsync(owner, workerId, flockA)).StatusCode);

        // Two shapes exist only to page PAST the Worker's one assigned flock, so
        // a legitimately empty answer is expected there; for those the honest
        // check is "no foreign row", never "some row". Keeping them matters
        // because paging is exactly where a scope filter that is applied after
        // the window would leak.
        var shapes = new[]
        {
            $"?search={Q(marker)}&limit=500",
            $"?search={Q(marker)}&eligibility=all&limit=500",
            $"?search={Q(marker)}&includeArchived=true&limit=500",
            $"?search={Q(marker)}&eligibility=active&limit=500",
            "?eligibility=all&limit=2&offset=0",
            "?limit=500&offset=0",
        };
        var windowsPastA = new[] { "?limit=1&offset=1", "?limit=2&offset=5" };

        foreach (var query in shapes)
        {
            var asWorker = await worker.GetFromJsonAsync<List<FlockRow>>("/api/v1/flocks" + query);
            Assert.NotNull(asWorker);
            Assert.NotEmpty(asWorker);
            Assert.All(asWorker, r => Assert.Equal(flockA, r.Id));

            var asOwner = await owner.GetFromJsonAsync<List<FlockRow>>("/api/v1/flocks" + query);
            Assert.NotNull(asOwner);
            Assert.Contains(asOwner, r => r.Id == flockB || r.Id == archivedB);
        }

        foreach (var query in windowsPastA)
        {
            var asWorker = await worker.GetFromJsonAsync<List<FlockRow>>("/api/v1/flocks" + query);
            Assert.NotNull(asWorker);
            Assert.All(asWorker, r => Assert.Equal(flockA, r.Id));   // empty or A — never B

            var asOwner = await owner.GetFromJsonAsync<List<FlockRow>>("/api/v1/flocks" + query);
            Assert.NotNull(asOwner);
            // The Owner's own window is non-empty only where rows exist at that
            // offset; the discriminating check is that B is still reachable
            // unscoped, which the shapes above already proved.
            var scoped = new[] { flockA, flockB, archivedB }.ToHashSet();
            Assert.All(asOwner, r => Assert.Contains(r.Id, scoped));
        }

        // A search naming the unassigned flock specifically is EMPTY for the
        // Worker — never silently satisfied by a neighbouring row.
        var exact = await worker.GetFromJsonAsync<List<FlockRow>>(
            "/api/v1/flocks?search=" + Q("Bravo " + marker) + "&limit=500");
        Assert.NotNull(exact);
        Assert.Empty(exact);
    }

    // The repository-layer twin: the same predicate set under a hand-built
    // Worker scope, so a leak the HTTP surface happens to mask (an upstream
    // guard, an extra filter) still goes red.
    [Fact]
    public async Task DiscoveryPredicate_UnderWorkerScope_ExcludesUnassignedFlock()
    {
        var marker = "rp" + Guid.NewGuid().ToString("N");
        var accountId = await factory.SeedAccountWithUserAsync($"o4-{Guid.NewGuid():N}@test.local");
        var (flockA, flockB, _) = await SeedScopedFlocksAsync(accountId, marker);

        var scope = new FlockScope();
        scope.Resolve(false, [flockA]);
        var tenant = new TenantContext();
        tenant.Resolve(accountId);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(factory.ConnectionString)
            .Options;
        await using var db = new AppDbContext(options, tenant, scope);

        var found = await db.Flocks.AsNoTracking()
            .Where(f => f.Status == FlockStatus.Active || f.Status == FlockStatus.Depleted)
            .Where(f => EF.Functions.ILike(f.Name, "%" + marker + "%", "\\"))
            .OrderBy(f => f.Name).ThenBy(f => f.Id)
            .Skip(0).Take(50)
            .ToListAsync();

        Assert.NotEmpty(found);
        Assert.All(found, f => Assert.Equal(flockA, f.Id));
        Assert.DoesNotContain(found, f => f.Id == flockB);
    }

    // --- helpers ---------------------------------------------------------------

    private async Task<(Guid FlockA, Guid FlockB, Guid ArchivedB)> SeedScopedFlocksAsync(
        Guid accountId, string marker)
    {
        var flockA = Guid.NewGuid();
        var flockB = Guid.NewGuid();
        var archivedB = Guid.NewGuid();
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var a = Flock.Create(flockA, accountId, Guid.NewGuid(), Guid.NewGuid(),
                "Alpha " + marker, "Breed", new DateOnly(2026, 1, 1), 100);
            var b = Flock.Create(flockB, accountId, Guid.NewGuid(), Guid.NewGuid(),
                "Bravo " + marker, "Breed", new DateOnly(2026, 1, 1), 100);
            var ab = Flock.Create(archivedB, accountId, Guid.NewGuid(), Guid.NewGuid(),
                "Bravo Archived " + marker, "Breed", new DateOnly(2026, 1, 1), 100);
            ab.Deplete(new DateOnly(2026, 2, 1));
            ab.Archive(new DateOnly(2026, 3, 1));
            db.Flocks.AddRange(a, b, ab);
            db.Customers.Add(Customer.Create(Guid.NewGuid(), accountId, "Cust " + marker, "555-0101"));
            await db.SaveChangesAsync();
        });
        return (flockA, flockB, archivedB);
    }

    private static async Task<HttpResponseMessage> AssignFlockAsync(
        HttpClient client, Guid userId, Guid flockId)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v1/users/{userId}/flock-assignments")
        {
            Content = JsonContent.Create(new { flockId }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        // Step-up: recent password confirmation, minted from the harness
        // password the assigning Owner was created with (#338).
        var stepUp = await client.PostAsJsonAsync(
            "/api/v1/auth/step-up", new { password = TestHarness.Password });
        stepUp.EnsureSuccessStatusCode();
        request.Headers.Add(
            AuthEndpoints.StepUpHeaderName,
            (await stepUp.Content.ReadFromJsonAsync<Cluckwork.Api.Endpoints.Auth.StepUpResponse>())!.Token);
        return await client.SendAsync(request);
    }
}
