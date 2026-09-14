# Declare each adapter's module reach (#846)

> **Rule** — the one-paragraph version lives in [`AGENTS.md`](../../AGENTS.md);
> this file records the rationale and the limits of the guard.

**Status:** accepted
**Date:** 2026-09-14

## What happened

No incident. This is epic #514, slice 4, Track B, stacked on #845's table-owner
ledger. The module ledger treats Platform as a free hub, so it cannot detect an
endpoint or infrastructure adapter acquiring another business module's port.

The baseline walk finds **397 adapter declarations and 150 non-empty adapter
rows**. Rows come from `AdapterReachScanner.RenderAdapters`, rather than a manual
inventory. `ExpenseEndpoints.ListExpenses` reaches Farm, Finance,
FlockManagement, and Insights. Its `IAuditEventRepository` belongs to Insights
under the existing ledger; Platform owns the domain audit types.

## The rule

Declare each adapter's set of module-kind owners in `module-ledger.json` and
review any newly reached owner before extending that set. A live owner outside
the declared set fails the guard, even if the total owner count stays the same.
Removing a crossing, deleting an adapter, or leaving an unused allowance stays
green and appears in `Loosenable` for pruning. Platform owners do not count.
Reject `AppDbContext`, `DbContext`, `DbSet`, and `IQueryable` in endpoint parameters
or service resolutions regardless of declared reach. This persistence ban applies
only under `Cluckwork.Api.Endpoints`. CLI verbs, jobs, and seeders legitimately
hold persistence types for migration, maintenance, and seeding; those types do
not themselves count as module reach there and do not fail the guard.

The ledger's `adapterRoots` declares three namespace subtrees and two exact
seeder types. Its `persistenceForbiddenNamespaces` declares the endpoint-only
ban. The scanner does not hardcode these roots. Every method, regardless of
accessibility or static status, and every declared constructor counts. Primary
constructors count too. A nested type under a namespace root is included;
the two exact seeder roots select those types themselves.

## Why not the obvious alternative

This is a ceiling, not a ban on reaching multiple modules. An immediate ban
would require more than forty exemptions before it could pass on this tree.
The ceiling records the current reach and requires review when it grows.
An allowance remains usable until someone prunes it; the guard does not store
a historical minimum outside Git.

The adapter definition deliberately over-approximates runtime entry points.
Private endpoint helpers and request-record constructors count even when the
HTTP router never invokes them directly. That can raise the recorded ceiling,
but it avoids hiding dependencies behind a remembered list of routed handlers.
Lambdas and local functions are not independent adapters. Typed lambda
parameters, including inline route handlers, and service resolutions inside a
method's lambdas or local functions are attributed to that method. Untyped
lambda parameters cannot be resolved and are ignored. Review found that the
initial walk missed typed lambda parameters; adding them generated three new
mapping-method rows for Products, Egg Grades, and Inventory, increasing the
ledger from 147 to 150 rows without changing an existing row.
Overloads share the specified enclosing-type-plus-member key and their reach
is combined. Moving a file or adding a comment does not change the member key. Generic
enclosing types retain their arity in that key.

Applying the persistence ban to every adapter would fail on existing migration
verbs, background sweeps, and the simulation seeder. The accepted scope is an
endpoint-only ban, with no source refactoring or persistence exceptions ledger.

## What this does NOT cover

The walk is syntax-only and does not boot a host or bind a Roslyn compilation.
It reads method and constructor parameter types, typed lambda parameters in
their bodies, and each type's generic arguments. It reads generic type arguments
and direct `typeof` arguments of `GetRequiredService`, `GetService`,
`GetRequiredKeyedService`, and `GetKeyedService`, plus generic type arguments
of `ActivatorUtilities.CreateInstance`. It does not follow return types,
fields, properties, arbitrary object creation, service types passed through
variables instead of `typeof`, reflection, inferred types, dependency
forwarding, or the transitive dependencies of an injected handler. Primary constructors
contribute their parameter types, not field initializers.

Owner resolution reuses the module ledger's longest namespace prefix. Exact
namespace claims also cover referenced types beneath them. Fully qualified names,
file-scoped imports, namespace-scoped imports, aliases, and relative namespace
names are resolved syntactically. A declaration index lets simple names resolve
through an import or to a type in the same namespace or enclosing type. The
scanner does not bind overloads, generic constraints, assembly references,
extern aliases, or competing imports. It does not expand imports from another
file. The existing module-ledger guard rejects global module imports. Both
walks share the same .NET 10, DEBUG, and TRACE parse symbols; other conditional
compilation branches are outside this walk.

A simple name without a matching import or local declaration is ignored and
listed in `UnresolvedTypes`; it is never assigned to an arbitrary imported
module. The baseline has 619 such references, covering these 37 external names,
and **no unresolved Cluckwork type**:

`Action`, `CancellationToken`, `ClaimsPrincipal`, `CookieOptions`, `DateOnly`,
`DateTimeOffset`, `EndpointFilterDelegate`, `EndpointFilterInvocationContext`,
`EntityTagHeaderValue`, `Func`, `Guid`, `HttpContext`, `HttpRequest`, `HttpResponse`,
`IAuthorizationService`, `IEnumerable`, `ILogger`, `IOptions`, `IOptionsSnapshot`,
`IReadOnlyDictionary`, `IReadOnlyList`, `IServiceProvider`, `IServiceScopeFactory`,
`IValidator`, `IWebHostEnvironment`, `JsonElement`, `List`, `ModelBuilder`,
`NpgsqlConnection`, `PipeWriter`, `RouteGroupBuilder`, `Stream`, `Task`,
`TimeProvider`, `TimeSpan`, `UserManager`, and `WebApplication`.

The persistence check recognizes the four type names syntactically, including
qualified names and aliases. It does not prove inheritance from `DbContext`.
Generic arguments are still walked independently, including those inside a
persistence container. #843's adapter-tier concept layers on top of this later;
this guard assigns no tier and grants no runtime authorization. It changes no
`src/` code, CI workflow, or package dependency.

## How it is enforced

`tests/Cluckwork.Application.Tests/Architecture/AdapterReachTests.cs` exercises
the scanner on temporary trees. `AdapterReachRealTreeTests` gates the real
`src/` tree. Parse errors make the walk untrusted. Duplicate adapter symbols,
blank symbols, unknown reach owners, and platform reach owners are registry
errors. The real-tree floor is 40 adapters, independently asserted by the
second real-tree test. Temporary fixtures use their own adapter count as the
floor.

An undeclared crossing reports the adapter, owner, crossing type, and
`file:line`, followed by the generated JSON row to review. Rows and owner lists
are sorted ordinally. `Loosenable` never enters the gate's failure list.
`AdapterReachRealTreeTests.AssertNoLoosenable` is a test-only assertion for an
explicit pruning pass, and the regular gate prints the pruning list without
asserting that it is empty.

Run the gate and print its measured count and pruning list with:

```sh
dotnet test tests/Cluckwork.Application.Tests \
  --filter FullyQualifiedName~AdapterReachRealTreeTests \
  --logger 'console;verbosity=detailed'
```

Five real-tree mutations were run against the built syntax scanner with
`--no-build`, so compiler failures from intentionally incomplete edits could
not substitute for guard failures. Each was reverted with `git checkout -- src`.
The output files are local evidence under `/tmp/514/mutations-846/`:

| Mutation | Result | Output file |
|---|---|---|
| Add `IProductRepository products` to `ListExpenses` | RED, `ListExpenses -> Commerce` | `1-commerce-parameter.txt` |
| Add `AppDbContext db` to `ListExpenses` | RED, forbidden persistence type at `ListExpenses` | `2-endpoint-dbcontext.txt` |
| Remove `IFlockRepository flocks` from `ListExpenses` | GREEN, `Loosenable` names `ListExpenses -> FlockManagement` | `3-remove-flock-parameter.txt` |
| Resolve `CreateFlockHandler` in `MigrateCliCommand.RunAsync` | RED, `MigrateCliCommand.RunAsync -> FlockManagement` | `4-cli-flock-service.txt` |
| Add `AppDbContext db` to the inline deactivate handler in `MapEggGradeEndpoints` | RED, forbidden persistence type at `EggGradeEndpoints.cs:37` | `5-lambda-dbcontext.txt` |
