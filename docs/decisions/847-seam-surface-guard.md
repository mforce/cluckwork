# No persistence type crosses an Application seam (#514, #847)

> **Rule** — the one-paragraph version lives in [`AGENTS.md`](../../AGENTS.md);
> this file is the relocated rationale (what shipped, why the short version was
> insufficient, what not to break).

**Status:** accepted
**Date:** 2026-09-14

## What happened

No incident. This is Track B of the modular-monolith epic (#514), landed early
because it is cheapest before the first module contract exists: there are zero
`IQueryable` returns in `Cluckwork.Application` today, the assembly references no
EF package, and so the guard starts with no exemptions and nothing to argue about.
Added after the first contract ships, it would arrive with a backlog.

## The rule

A public interface in `Cluckwork.Application.Features.*` or
`Cluckwork.Application.Common` may not expose persistence anywhere in its
signatures. `SeamSurfaceRealAssemblyTests` reflects over every such interface,
including the members it inherits from base interfaces (with substituted type
arguments), operators, indexers, events and its own generic constraints,
recursing through parameters, return types, generic arguments, delegates,
function pointers and the public properties of any `Cluckwork.*` type it
reaches, and fails on `DbContext`,
`DbSet<>`, `IQueryable`, any `Microsoft.EntityFrameworkCore` type, the bases
`Entity` and `AggregateRoot` as a declared type, or any `Cluckwork.Infrastructure`
type. A second assertion pins that the Application assembly references no
`Microsoft.EntityFrameworkCore*`, `Npgsql` or `Cluckwork.Infrastructure` assembly,
which is what makes the first assertion's `DbSet` and `DbContext` rows more than
vacuous. Return a DTO, a value object, a concrete aggregate or a paged result.
Break it and a repository that returns a deferred query hands its caller the
tenant filter, the change tracker and the connection lifetime along with the rows.

## Why not the obvious alternative

**Waiting for the contracts.** The design's §8 rule 5 puts this check on the
module contracts once they exist. Those are Track C, unauthorised. The interfaces
that exist today are what a contract will be allowed to say, and pinning them now
costs nothing.

**A Roslyn walk like the module ledger.** Reflection is the right tool here: the
test project already references the Application assembly, generic arguments and
property types come back resolved rather than as syntax, and a `using` alias
cannot hide a type from it.

**Forbidding aggregates.** Repository interfaces return concrete aggregates
(`Task<Flock?>`) by design today. Only the bases are forbidden as declared types;
narrowing what a seam may return to snapshots is a contract decision, not this
guard's.

## What this does NOT cover

`Cluckwork.Infrastructure` internals, where EF types are the point. Public
classes in Application (handlers, validators) rather than interfaces. Whether a
returned aggregate should have been a snapshot.

## How it is enforced

`tests/Cluckwork.Application.Tests/Architecture/SeamSurfaceRealAssemblyTests.cs`,
in the `application` leg of the CI matrix. The fixture tests in
`SeamSurfaceTests.cs` pin each forbidden shape on its own named assertion, and the
walk fails closed on a prefix that matches fewer than 30 interfaces. Mutation
output for the real assembly is attached to the PR that landed this record.
