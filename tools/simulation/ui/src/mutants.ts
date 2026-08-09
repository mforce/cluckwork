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
// logic directly. A regression purely inside the SPA — a role gate deleted from
// `nav.tsx`, say — is not reachable by rewriting a server response.
//
// An earlier version of this comment claimed the nav-gate assertions were
// "covered by spec-level vacuity mutants" instead. **That was false** — no such
// mutant existed, and three specs' role-gate assertions had no mutation coverage
// of any kind while a reader was told otherwise (PR #390 review, found
// independently by three reviewers). The lesson is the repo's own: a comment
// claiming more than the code delivers is a defect, and this one was actively
// hiding the gap it described.
//
// `nav-role-gate-bypassed` was added to close it, and **it does not**. Read this
// before trusting the mutation score on the nav gates.
//
// The idea was sound on paper: the SPA derives its role by base64-decoding the
// JWT payload WITHOUT verifying the signature (`web/src/auth/claims.ts` —
// display-only, deliberately), so rewriting the `role` claim should change what
// the nav renders. What actually happens is that the forged token is rejected by
// the SERVER on the next call, the authenticated bootstrap never completes, and
// the spec dies in `signIn` on `expect(getByRole("complementary"))` — before it
// ever looks at a nav link.
//
// So the mutant IS killed, and the kill proves nothing about the role gate. It
// is kept because a red is still better than a green here, and removing it would
// leave the guarantee with no mutant at all — but it is recorded as what it is.
//
// This is the SECOND time this particular claim has had to be walked back (the
// first was a comment describing mutants that did not exist). The pattern worth
// naming: a client-side gate whose input the server also validates cannot be
// mutated from the network boundary, because breaking the input breaks the
// session first. Closing it properly needs a build-time source mutation.
//
// STILL UNCOVERED, and named rather than left silent:
//   * the nav role gates — for the reason above;
//   * the in-memory-token guarantee (#145) — a purely client-side property;
//   * the PWA specs — see 277-decisions.md on why `sw.js` cannot be mutated here.
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
  "stock-pager-inert": {
    breaks:
      "offset paging on the lot list (#465) — every page request is rewritten to offset 0, so "
      + "load more re-serves page one forever and older lots stay unreachable (the pre-#465 "
      + "behavior; the SPA's id-dedupe silently appends nothing)",
    caughtBy: "readonly.spec.ts — pages a deep grade's lots with load more (#465)",
    apply: async (page) => {
      await page.route("**/api/v1/stock/lots**", async (route) => {
        const url = new URL(route.request().url());
        const offset = url.searchParams.get("offset");
        if (offset === null || offset === "0") return route.fallback();
        url.searchParams.set("offset", "0");
        const response = await route.fetch({ url: url.toString() });
        await route.fulfill({ response });
      });
    },
  },

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

  // --- role gates ----------------------------------------------------------
  "nav-role-gate-bypassed": {
    breaks:
      "the role claim the nav gate reads. NOTE: in practice this breaks the SESSION rather than "
      + "the gate — the server rejects the forged token and sign-in never completes, so the kill "
      + "does not prove the nav assertion. See the header.",
    caughtBy: "readonly.spec.ts — is not offered the destinations it cannot use (kills in signIn)",
    apply: async (page) => {
      // BOTH login AND refresh. Forging only the login response does not work,
      // and finding out why is the useful part: the SPA's bootstrap issues a
      // refresh straight after signing in, and the genuine token that comes back
      // REPLACES the forged one before the nav ever renders. The first version of
      // this mutant did exactly that and survived — which looked like a spec
      // defect and was really an incomplete mutant (PR #390 review, caught by the
      // harness's own survivor report rather than by reading it).
      const forgeRole = async (route: Parameters<Parameters<typeof page.route>[1]>[0]) => {
        const response = await route.fetch();
        if (!response.ok()) return route.fulfill({ response });
        const body = await response.json().catch(() => null);
        if (!body?.accessToken) return route.fulfill({ response });
        // Re-stamp the role claim as Admin. The SPA never verifies the signature
        // (claims.ts decodes for display only), so the nav believes it — exactly
        // the state a deleted role gate would produce.
        const [header, payload, signature] = String(body.accessToken).split(".");
        const claims = JSON.parse(Buffer.from(payload!, "base64url").toString());
        claims.role = "Admin";
        const forged = Buffer.from(JSON.stringify(claims)).toString("base64url");
        await route.fulfill({
          status: response.status(),
          contentType: "application/json",
          body: JSON.stringify({ ...body, accessToken: [header, forged, signature].join(".") }),
        });
      };
      await page.route("**/api/v1/auth/login", forgeRole);
      await page.route("**/api/v1/auth/refresh", forgeRole);
    },
  },

  // --- multi-step business flows -------------------------------------------
  "payment-never-settles": {
    breaks: "payment application, so a fully-paid order still reports an outstanding balance",
    caughtBy: "sales.spec.ts — takes an order from new customer through to a recorded payment",
    apply: async (page) => {
      await page.route("**/api/v1/sales/**", async (route) => {
        const response = await route.fetch();
        const ct = response.headers()["content-type"] ?? "";
        if (!response.ok() || !ct.includes("json")) return route.fulfill({ response });
        const body = await response.json().catch(() => null);
        if (!body || typeof body !== "object") return route.fulfill({ response });
        // Leave a balance outstanding no matter what was paid. The spec's real
        // assertion — the "record payment" affordance being withdrawn — must fail.
        if ("outstandingMinorUnits" in body) {
          await route.fulfill({
            status: response.status(),
            contentType: "application/json",
            body: JSON.stringify({ ...body, outstandingMinorUnits: 1 }),
          });
          return;
        }
        await route.fulfill({ response });
      });
    },
  },

  "export-returns-nothing": {
    breaks: "the export body, so the download arrives empty",
    caughtBy: "owner.spec.ts — export downloads a real file",
    apply: async (page) => {
      await page.route("**/api/v1/export/**", async (route) => {
        await route.fulfill({
          status: 200,
          contentType: "application/octet-stream",
          headers: { "content-disposition": 'attachment; filename="empty.zip"' },
          body: "",
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
