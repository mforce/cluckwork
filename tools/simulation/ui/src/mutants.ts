// tools/simulation/ui/src/mutants.ts — the mutation harness.
//
// ================== WHAT THIS IS FOR ==================
//
// A passing E2E suite proves nothing on its own. The failure this guards against
// is an assertion that CANNOT FAIL — a locator that silently matches something
// harmless, an `expect` on a value that is constant, a check that was true before
// the feature existed. Those pass forever and read exactly like coverage.
//
// So each high-value guarantee gets a MUTANT: the application's behaviour is
// broken at the network boundary, in the specific way a real regression would
// break it, and the spec that claims to cover it must go RED. A mutant that
// survives means the spec is wrong, and is reported as such rather than quietly
// dropped.
//
// ================== WHY AT THE NETWORK BOUNDARY ==================
//
// The honest ideal is to mutate the app's own source and rebuild. That costs a
// container image build per mutant — minutes each — which in practice means the
// mutation check gets run once and never again.
//
// These mutants instead rewrite the SERVER'S ANSWER, which is a faithful stand-in
// for the regressions actually worth catching here: every guarantee in the
// persona specs is ultimately "the server refused / permitted this, and the
// screen reflected it". A mutant that makes `/audit` return 200 with rows IS what
// "somebody removed the authorization policy" looks like from the browser.
//
// The limit, stated plainly rather than glossed: this cannot mutate CLIENT-side
// logic. A regression purely inside the SPA — a role gate deleted from nav.tsx,
// say — is not reachable this way, so the nav-gate assertions are covered by
// spec-level vacuity mutants (see MUTANTS below) rather than behavioural ones.
//
// ================== SAFETY ==================
//
// Inert unless `CLUCKWORK_E2E_MUTANT` is set. When it IS set, preflight prints a
// banner and every test is annotated, because the one genuinely dangerous outcome
// here is a mutation run being mistaken for a real one — a green result under a
// mutant is not a pass, it is a SURVIVING MUTANT.

import type { Page } from "@playwright/test";

export const MUTANT_ENV = "CLUCKWORK_E2E_MUTANT";

export interface Mutant {
  /** What regression this imitates. */
  readonly breaks: string;
  /** The spec that must go RED. Used by the runner, and by a reader asking "who covers this?". */
  readonly caughtBy: string;
  readonly apply: (page: Page) => Promise<void>;
}

/** Fulfil a request with a canned JSON body — the shape ProblemDetails-free endpoints return. */
async function json(page: Page, pattern: string, status: number, body: unknown): Promise<void> {
  await page.route(pattern, async (route) => {
    await route.fulfill({
      status,
      contentType: "application/json",
      body: JSON.stringify(body),
    });
  });
}

export const MUTANTS: Record<string, Mutant> = {
  // --- authorization boundaries -------------------------------------------
  "audit-gate-removed": {
    breaks: "the server-side AdminOnly policy on /audit, so a ReadOnly deep link succeeds",
    caughtBy: "readonly.spec.ts — is refused server-side on a direct link to /audit",
    apply: (page) =>
      json(page, "**/api/v1/audit**", 200, {
        items: [
          {
            id: "00000000-0000-0000-0000-000000000001",
            occurredAtUtc: "2026-08-01T00:00:00Z",
            actorEmail: "leaked@example.test",
            action: "User.Login",
            entityType: "User",
            entityId: "00000000-0000-0000-0000-000000000002",
            reason: null,
          },
        ],
        hasMore: false,
      }),
  },

  "users-gate-removed": {
    breaks: "the server-side gate on /users, so a ReadOnly deep link lists real users",
    caughtBy: "readonly.spec.ts — is refused server-side on a direct link to /users",
    apply: (page) =>
      json(page, "**/api/v1/users**", 200, [
        {
          id: "00000000-0000-0000-0000-000000000003",
          email: "leaked@example.test",
          displayName: "Leaked User",
          role: "Admin",
        },
      ]),
  },

  "flock-scope-removed": {
    breaks: "FlockScope enforcement, so a restricted worker's write to an unassigned flock succeeds",
    caughtBy: "worker.spec.ts — is refused a daily entry on a flock it is not assigned to (#388)",
    apply: async (page) => {
      await page.route("**/api/v1/daily-entries", async (route) => {
        if (route.request().method() !== "POST") return route.fallback();
        await route.fulfill({
          status: 201,
          contentType: "application/json",
          body: JSON.stringify({ id: "00000000-0000-0000-0000-000000000004" }),
        });
      });
    },
  },

  // --- data integrity on screen -------------------------------------------
  "stock-summary-broken": {
    breaks: "the stock summary fetch, so the dashboard's stat tiles fall back to their em-dash",
    caughtBy: "owner.spec.ts — dashboard shows real production, stock and sales data",
    apply: (page) =>
      json(page, "**/api/v1/stock**", 500, { title: "Server error", status: 500 }),
  },

  // --- documented bounds ---------------------------------------------------
  "report-range-bound-removed": {
    breaks: "the MaxRangeDays check, so a range past the documented bound is served instead of refused",
    caughtBy: "reports-range.spec.ts — refuses one day beyond the documented bound",
    apply: async (page) => {
      await page.route("**/api/v1/reports/**", async (route) => {
        const response = await route.fetch();
        if (response.status() !== 400) return route.fulfill({ response });
        // Pretend the over-wide range was accepted and returned an empty report:
        // the exact failure #311's bound exists to prevent being silent.
        await route.fulfill({
          status: 200,
          contentType: "application/json",
          body: JSON.stringify({ rows: [], gradeTotals: [], totals: {} }),
        });
      });
    },
  },

  // --- session integrity ---------------------------------------------------
  "refresh-always-fails": {
    breaks: "the silent refresh, so an expired access token strands the user instead of renewing",
    caughtBy: "session-refresh.spec.ts — forces a 401: the app refreshes and retries",
    apply: (page) =>
      json(page, "**/api/v1/auth/refresh", 401, { title: "Unauthorized", status: 401 }),
  },

  "logout-not-honoured": {
    breaks: "server-side logout, so the refresh cookie survives sign-out and can restore the session",
    caughtBy: "session-races.spec.ts — a logout during an in-flight refresh cannot be resurrected",
    apply: async (page) => {
      await page.route("**/api/v1/auth/logout", async (route) => {
        // Answer OK without ever reaching the server, so the cookie is never
        // revoked — exactly what a silently-failing revoke looks like.
        await route.fulfill({ status: 204, body: "" });
      });
    },
  },
};

/** The mutant named by the environment, or null for an ordinary run. */
export function activeMutant(env: NodeJS.ProcessEnv = process.env): { name: string; mutant: Mutant } | null {
  const name = env[MUTANT_ENV]?.trim();
  if (!name) return null;
  const mutant = MUTANTS[name];
  if (!mutant) {
    throw new Error(
      `${MUTANT_ENV}="${name}" is not a known mutant. Known: ${Object.keys(MUTANTS).join(", ")}`,
    );
  }
  return { name, mutant };
}
