// tools/simulation/ui/src/preflight.ts — the suite's globalSetup.
//
// Runs before the first browser starts and refuses to go on unless the stack is
// up AND the fixture actually contains the data the specs are about to assert on.
//
// ================== WHY THIS IS NOT PARANOIA ==================
//
// This is the same lesson `verify-harness.sh` was written for (#370): the sim
// harness is dev tooling nothing automated exercises, so it drifts, and the
// failures it produces point nowhere near their cause. A half-seeded database
// does not announce itself — it produces a dashboard with no rows, and a suite
// that then fails with "expected a table, found `dashboard:noStockMessage`",
// which reads exactly like a UI regression. Somebody would go looking in the SPA.
//
// The distinction that matters here is between *the app is broken* and *the
// fixture is not there*. Only the first is a finding. Everything below exists to
// make the second one impossible to mistake for it.
//
// ================== WHAT IT DELIBERATELY DOES NOT DO ==================
//
// It does not re-implement the seeder's manifest validation, and it does not
// assert exact counts. `SimulationDataSeeder` already validates its own fixture
// exactly and fails closed; duplicating that here would be a second, drifting
// copy of the rules — the trap `verify-harness.sh` documents at length and stays
// out of. This asserts only the shape the SPECS depend on: enough flocks for the
// restricted-worker comparison, non-empty production history, at least one
// confirmed order to settle. Sufficient, not exhaustive.
//
// It also does not RUN `reset.sh`. The suite is authorised to, but doing it from
// globalSetup would mean a five-minute wipe fired by a typo'd `--grep`. Reseeding
// is a decision, so it stays an explicit `run-e2e.sh --reset`.

import type { FullConfig } from "@playwright/test";
import { apiGet, isReady, signInForToken } from "./api";
import { loadCast, owner, restrictedWorker, unrestrictedWorker } from "./cast";
import { describeBrowser, resolveBrowser } from "./browser";
import { farmContext } from "./farm";
import { BASE_URL } from "./env";

interface FlockRow { id: string; name: string }
interface Paged<T> { items?: T[] }

function fail(what: string, remedy: string): never {
  throw new Error(
    `\nPREFLIGHT FAILED: ${what}\n\n  ${remedy}\n\n`
      + `  Nothing was run. This is the harness or the fixture, not the app.\n`,
  );
}

function countOf<T>(res: T[] | Paged<T>): number {
  return Array.isArray(res) ? res.length : (res.items?.length ?? 0);
}

export default async function preflight(_config: FullConfig): Promise<void> {
  const lines: string[] = [];

  // 1. Is the stack up? /health/ready is the honest probe: it returns 503 while
  //    migrations are pending (#263), so "the port answers" is not mistaken for
  //    "the schema is current".
  const ready = await isReady();
  if (!ready.ok) {
    fail(
      `${BASE_URL}/health/ready is not green (${ready.detail}).`,
      `Start the stack: bash tools/simulation/reset.sh`,
    );
  }
  lines.push(`stack:    ${BASE_URL} (/health/ready green)`);

  // 2. Does the cast file exist, and do its credentials still work? A stale
  //    .sim-cast.json outliving the database that matched it is a KNOWN failure
  //    mode here — the file is git-ignored, so it survives a reseed that
  //    regenerated the passwords (README: "One trap worth knowing").
  const cast = loadCast();
  let ownerToken: string;
  try {
    ownerToken = await signInForToken(owner());
  } catch (cause) {
    fail(
      `the Owner in .sim-cast.json cannot sign in — the cast file and the database disagree.`,
      `Regenerate and reseed: bash tools/simulation/bootstrap.sh --force && bash tools/simulation/reset.sh`
        + `\n  (underlying: ${(cause as Error).message})`,
    );
  }
  lines.push(`cast:     ${cast.length} personas, Owner sign-in OK`);

  // 3. Are the personas the specs name actually present? castMember() throws a
  //    precise message; calling them here means that message arrives before a
  //    browser starts rather than inside one spec's setup.
  restrictedWorker();
  unrestrictedWorker();

  // 4. Is the fixture populated? Only the shapes the specs rely on.
  const flocks = await apiGet<FlockRow[]>(ownerToken, "/flocks");
  if (countOf(flocks) < 2) {
    fail(
      `the fixture has ${countOf(flocks)} flock(s); the restricted-worker specs need 2.`,
      `Reseed: bash tools/simulation/reset.sh`,
    );
  }

  const entries = await apiGet<Paged<unknown>>(ownerToken, "/daily-entries?limit=5");
  if (countOf(entries) === 0) {
    fail(
      `the fixture has no daily entries, so every populated-screen assertion would be vacuous.`,
      `Reseed: bash tools/simulation/reset.sh`,
    );
  }

  const orders = await apiGet<Paged<unknown>>(ownerToken, "/sales?limit=5");
  if (countOf(orders) === 0) {
    fail(
      `the fixture has no sales orders, so the Sales persona has nothing to open.`,
      `Reseed: bash tools/simulation/reset.sh`,
    );
  }
  lines.push(`fixture:  ${countOf(flocks)} flocks, daily entries present, sales orders present`);

  // 5. The farm clock. Resolved here so an unresolvable zone fails once, loudly,
  //    instead of once per date field.
  const farm = await farmContext();
  lines.push(`farm:     "${farm.name}" tz=${farm.timeZoneId} locale=${farm.locale}`);

  // 6. Which browser binary is about to run. Printed, never inferred — the two
  //    paths (system chromium here, downloaded on a runner) are easy to confuse
  //    when reading a report after the fact.
  lines.push(describeBrowser(resolveBrowser()));

  process.stdout.write(`\n${lines.map((l) => `  ${l}`).join("\n")}\n\n`);
}
