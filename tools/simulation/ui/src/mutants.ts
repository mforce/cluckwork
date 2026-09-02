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
// ================== THE SECOND BOUNDARY: DOM-LEVEL MUTANTS (#501) ==================
//
// The eleven `a11y-*` mutants below do NOT go through the network. They inject a
// script into the page and break the DOM the way the corresponding regression
// would leave it. That was added for #501, whose guarantees are entirely
// client-side — a live region's presence in the accessibility tree is never
// something a server response can decide — and it is a different instrument
// from the rest of this file, so read it as one.
//
// **What it is faithful to, and what it is not.** These imitate the EFFECT of a
// regression, not its cause: the app's own source is untouched, so a mutant
// proves the spec notices that DOM state, not that the spec would notice the
// real code change if that change happened to leave a different trace. For
// `a11y-inert-sweep-removed` the two coincide exactly — reverting #483's sweep
// IS "no `inert` attribute on the background", which is what the mutant
// produces. For the four announcer mutants the match is close but not identical:
// the real regression is #499's hook deciding to write when it should not, and
// the mutants write the same text from outside. Both leave the screen reader in
// the same state, which is the state the spec judges.
//
// **Eleven mutants for one spec, because each earlier version covered a fraction
// of the test it named.** The four announcer mutants differ only in WHEN they
// write, and each is the only one that reaches its assertion:
//   * `a11y-announcer-duplicates-banner` writes from first paint, so it kills
//     the ordinary-path check — and kills it BEFORE the dialog loop below runs,
//     which left the anti-nag half of that test (half its title) unchecked.
//   * `a11y-announcer-renags-on-close` writes only on a close transition, so
//     the loop is what catches it.
//   * `a11y-announcer-writes-transiently` writes and clears within ~80ms, which
//     every point-in-time read in the test passes over; only the cumulative
//     MutationObserver assertion sees it.
//   * `a11y-announcer-writes-late` writes once, well after the last close, so
//     it is the only one that validates that assertion's final drain.
// The two inert mutants split the same way: `a11y-inert-sweep-removed` breaks
// the marking, `a11y-inert-never-lifted` breaks the UN-marking, and neither
// reaches the other's assertion. The last two are the odd ones out, and both
// exist because an assertion nothing can falsify is not an assertion:
// `a11y-dialog-hidden-from-tree` breaks the spec's own CONTROL, and the four
// `a11y-probe-*` mutants break a RECORDED BROWSER FACT rather than any product
// behaviour.
//
// **Four of them for one fact, because that fact has four independent sides.**
// "aria-live=off suppresses role=alert" means nothing unless an otherwise
// identical element still reports assertive, so the evidence is a PAIR, and a
// pair has four things that can rot: each probe's role, and each probe's
// resolved politeness. Three review rounds each found the next side uncovered
// (14: the fact; 15: the control; 16: the control's second assertion, sitting
// unreachable behind a hard assertion on its first). The spec's four FACT 1
// assertions are therefore `expect.soft` and individually named SIDE 1..4, and
// each mutant declares exactly the side(s) it must die on:
//   * `a11y-probe-alert-control-broken` strips the plain probe's role — SIDES
//     1 AND 2, which cannot be separated this way (no role, no politeness).
//   * `a11y-probe-alert-control-silenced` silences the plain probe while
//     keeping its role — SIDE 2 alone, which is what makes SIDE 2 falsifiable
//     independently of SIDE 1.
//   * `a11y-probe-off-role-dropped` strips the OFF probe's role — SIDE 3.
//   * `a11y-probe-live-off-ignored` strips the OFF probe's aria-live — SIDE 4.
//
// Each hole was found by a review asking the same question of the previous
// fix — thirteen rounds of it, and it was still producing findings at the end,
// so do not read this list as exhausted. The last one is the sharpest example:
// FACT 1's assertions, including the load-bearing `role="alert"` control, had
// no mutant reaching them at all and could have been deleted without changing
// the verdict — found only because the review was explicitly asked to re-read
// the a11y half, which twelve rounds of findings elsewhere had left untouched.
// Every time, the harness reported a clean kill and said nothing, because a
// mutant killing SOMETHING is not evidence that it killed the thing its name
// claims. `EXPECT_MSG_FOR` in mutation-check.sh now asks that question
// mechanically.
//
// Do not reach for this shape when a network mutant would do. It is here
// because the alternative for #501 was no mutation coverage at all.
//
// **A DOM mutant that fails to install looks exactly like a surviving one.**
// The first two below originally observed `document.documentElement`, which is not
// yet there when an init script runs: the constructor threw into `pageerror`,
// nothing was mutated, and both specs stayed green — reported as two
// survivors, which reads as "your specs are vacuous" rather than "your mutant
// never ran". Observing `document` fixes it. When a DOM mutant survives, check
// for a page error before touching the spec it accuses.
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
    breaks: "the stock summary fetch, so the dashboard's Stock panel degrades to its load error",
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

  // --- accessibility under a modal (#485/#501, DOM-level — see the header) --
  "a11y-inert-sweep-removed": {
    breaks:
      "#483's modal inert sweep, so the page behind a dialog stays in the accessibility tree — "
      + "the state the app was in before #483, and the state #485's premise denies",
    caughtBy:
      "a11y-live-regions.spec.ts — the offscreen announcers leave the accessibility tree while "
      + "a dialog is open",
    apply: async (page) => {
      await page.addInitScript(() => {
        // Strip `inert` as fast as the sweep sets it. Observing the DOCUMENT
        // with subtree covers body's children, which is what
        // syncModalBackground() actually marks, including ones added later —
        // `document.documentElement` does not exist yet at init-script time
        // (see the header). Removing an absent attribute produces no record,
        // so this terminates.
        new MutationObserver((records) => {
          for (const record of records) {
            if (record.target instanceof Element) record.target.removeAttribute("inert");
          }
        }).observe(document, {
          attributes: true,
          subtree: true,
          attributeFilter: ["inert"],
        });
      });
    },
  },

  "a11y-announcer-duplicates-banner": {
    breaks:
      "#499's rule that the offscreen announcer speaks ONLY for a message the visible banner "
      + "could not — here it mirrors the banner unconditionally, so a screen reader hears the "
      + "same warning from both regions",
    caughtBy:
      "a11y-live-regions.spec.ts — a standing farm warning is announced once by the banner",
    apply: async (page) => {
      await page.addInitScript(() => {
        const mirror = () => {
          const banner = document.querySelector("p.farm-warning");
          const region = document.querySelector('main.content > p.sr-only[aria-live="assertive"]');
          if (banner && region && region.textContent !== banner.textContent) {
            region.textContent = banner.textContent;
          }
        };
        new MutationObserver(mirror).observe(document, {
          childList: true,
          subtree: true,
          characterData: true,
        });
      });
    },
  },

  "a11y-announcer-renags-on-close": {
    breaks:
      "#499's retain rule specifically — the region is written only when the LAST dialog closes, "
      + "so a standing warning is re-announced on every close instead of once",
    caughtBy:
      "a11y-live-regions.spec.ts — a standing farm warning ... not re-announced as dialogs come "
      + "and go (the anti-nag loop, which a11y-announcer-duplicates-banner kills before reaching)",
    apply: async (page) => {
      // Deliberately distinct from `a11y-announcer-duplicates-banner`. That one
      // mirrors from first paint, so it kills the ordinary-path assertion
      // BEFORE the dialog loop ever runs — leaving the anti-nag half of the
      // test, which is half its title, with no mutation coverage at all (found
      // by an adversarial review of this PR, not by the harness). This one
      // stays silent until a close transition, so the loop is what catches it.
      await page.addInitScript(() => {
        let wasInert = false;
        const check = () => {
          const root = document.getElementById("root");
          const banner = document.querySelector("p.farm-warning");
          const region = document.querySelector('main.content > p.sr-only[aria-live="assertive"]');
          const nowInert = root?.hasAttribute("inert") ?? false;
          if (wasInert && !nowInert && banner && region) region.textContent = banner.textContent;
          wasInert = nowInert;
        };
        new MutationObserver(check).observe(document, {
          attributes: true,
          childList: true,
          subtree: true,
          attributeFilter: ["inert"],
        });
      });
    },
  },

  "a11y-announcer-writes-transiently": {
    breaks:
      "the announcer by writing the warning and clearing it again a frame later — a screen "
      + "reader still speaks it, but no snapshot of the DOM ever shows it",
    caughtBy:
      "a11y-live-regions.spec.ts — a standing farm warning ... not re-announced (the CUMULATIVE "
      + "MutationObserver assertion; every point-in-time read in that test passes under this)",
    apply: async (page) => {
      // This exists to prove one specific assertion is not decorative. The
      // spec's `recordAnnouncerWrites` observer was added because a read taken
      // at a fixed moment cannot see a write that lands outside it — but the
      // other two announcer mutants both trip an earlier point-in-time check,
      // so nothing demonstrated the observer could catch anything the snapshots
      // could not. A write that exists for ~80ms is exactly that case: audible,
      // and invisible to every snapshot in the test.
      await page.addInitScript(() => {
        let wasInert = false;
        const check = () => {
          const root = document.getElementById("root");
          const banner = document.querySelector("p.farm-warning");
          const region = document.querySelector('main.content > p.sr-only[aria-live="assertive"]');
          const nowInert = root?.hasAttribute("inert") ?? false;
          if (wasInert && !nowInert && banner && region) {
            region.textContent = banner.textContent;
            setTimeout(() => { region.textContent = ""; }, 80);
          }
          wasInert = nowInert;
        };
        new MutationObserver(check).observe(document, {
          attributes: true,
          childList: true,
          subtree: true,
          attributeFilter: ["inert"],
        });
      });
    },
  },

  "a11y-inert-never-lifted": {
    breaks:
      "the RETURN half of #483's sweep — the background is marked inert on open and never "
      + "un-marked on close, so the announcers stay out of the accessibility tree for good",
    caughtBy:
      "a11y-live-regions.spec.ts — the offscreen announcers leave the accessibility tree while "
      + "a dialog is open (specifically its final 'never returned' assertion)",
    apply: async (page) => {
      // The mirror of `a11y-inert-sweep-removed`, and it exists because that
      // one does NOT cover this: stripping `inert` as fast as it is set says
      // nothing about whether the sweep lifts it again. The spec asserted the
      // return path from the first draft, and no mutant reached that assertion
      // for four review rounds (found by an agent review of the codex round-1
      // fixes — the fourth instance on this PR of an assertion whose mutant
      // died somewhere else, which is why the harness now checks EXPECT_MSG_FOR).
      //
      // Re-adding on removal, rather than blocking the removal, keeps the
      // settling signal intact: `popModal` restores body overflow before it
      // calls the sweep, so `waitForModalEffect(false)` still resolves and the
      // test proceeds to the assertion this is aimed at.
      await page.addInitScript(() => {
        let sawInert = false;
        new MutationObserver(() => {
          const root = document.getElementById("root");
          if (root === null) return;
          if (root.hasAttribute("inert")) { sawInert = true; return; }
          // Only once a dialog has genuinely inerted it — otherwise this would
          // inert the page at load and break every test for the wrong reason.
          if (sawInert) root.setAttribute("inert", "");
        }).observe(document, {
          attributes: true,
          subtree: true,
          attributeFilter: ["inert"],
        });
      });
    },
  },

  "a11y-probe-live-off-ignored": {
    breaks:
      "the browser fact that `aria-live=\"off\"` suppresses the implicit politeness of "
      + "`role=\"alert\"` — the attribute is stripped from the probe after insertion, which is "
      + "what a Chromium that stopped honouring it would look like",
    caughtBy:
      "a11y-live-regions.spec.ts — recorded browser facts for the two deferred designs "
      + "(specifically FACT 1, the aria-live=\"off\" assertion and its role=alert control)",
    apply: async (page) => {
      // FACT 1 had no mutant at all: all the other a11y mutants target the
      // modal sweep, the control, or the announcer, and none of them touches
      // the injected probes. Its assertions — including the load-bearing
      // `role="alert"` control that must still report `assertive` — could have
      // been weakened or deleted without changing the mutation verdict (codex
      // round 13, answering a standing request to re-read the a11y half).
      //
      // Stripping the attribute rather than editing the spec is the faithful
      // shape: the recorded fact is "Chromium honours aria-live=off here", and
      // a Chromium that stopped would leave the element resolving to its
      // implicit assertive politeness, exactly as this does.
      await page.addInitScript(() => {
        const strip = () => {
          document.getElementById("probe-alert-off")?.removeAttribute("aria-live");
        };
        new MutationObserver(strip).observe(document, { childList: true, subtree: true });
      });
    },
  },

  "a11y-probe-alert-control-broken": {
    breaks:
      "FACT 1 SIDES 1 AND 2 — `role=\"alert\"` is stripped from the plain probe, so it neither "
      + "resolves to alert nor reports implicit assertive politeness",
    caughtBy:
      "a11y-live-regions.spec.ts — recorded browser facts (SIDE 1 and SIDE 2 together; the two "
      + "cannot be separated by removing the role, which is why SIDE 2 has its own mutant)",
    apply: async (page) => {
      // This one necessarily breaks two sides at once — an element with no
      // role has no implicit politeness either — so `EXPECT_MSG_FOR` requires
      // BOTH messages. That is only checkable because the spec's four FACT 1
      // assertions are soft; while SIDE 1 was hard, execution stopped there
      // and SIDE 2 could have been deleted with this mutant still counted as
      // killed (codex round 16, on round 15's fix).
      await page.addInitScript(() => {
        const strip = () => {
          document.getElementById("probe-alert-plain")?.removeAttribute("role");
        };
        new MutationObserver(strip).observe(document, { childList: true, subtree: true });
      });
    },
  },

  "a11y-probe-alert-control-silenced": {
    breaks:
      "FACT 1 SIDE 2 ALONE — the plain probe keeps `role=\"alert\"` but is given "
      + "`aria-live=\"off\"`, so it still resolves to alert while losing its implicit politeness",
    caughtBy:
      "a11y-live-regions.spec.ts — recorded browser facts (SIDE 2 only; SIDE 1 must still pass)",
    apply: async (page) => {
      // The isolating half of the pair above: same role, no politeness. If the
      // implicit-politeness assertion were weakened, nothing else in the suite
      // would notice — `a11y-probe-alert-control-broken` kills SIDE 1 first and
      // would report red regardless.
      await page.addInitScript(() => {
        const silence = () => {
          document.getElementById("probe-alert-plain")?.setAttribute("aria-live", "off");
        };
        new MutationObserver(silence).observe(document, { childList: true, subtree: true });
      });
    },
  },

  "a11y-probe-off-role-dropped": {
    breaks:
      "FACT 1 SIDE 3 — `role` is stripped from the OFF probe, so the pair no longer differs by "
      + "`aria-live` alone and the comparison is between two different elements",
    caughtBy:
      "a11y-live-regions.spec.ts — recorded browser facts (SIDE 3 alone; measured, not assumed — "
      + "SIDE 4 keeps PASSING under this mutant, because a paragraph carrying only "
      + "`aria-live=\"off\"` also reports no live property, which is precisely the ambiguity the "
      + "control exists to resolve)",
    apply: async (page) => {
      // `alertOff.role` was the one FACT 1 assertion with neither a message nor
      // a mutant (codex round 16). It is load-bearing: the recorded fact is
      // "same role, one attribute apart, different answers", and if the off
      // probe is not an alert then its `live === null` says nothing about
      // whether `aria-live="off"` suppressed anything.
      await page.addInitScript(() => {
        const strip = () => {
          document.getElementById("probe-alert-off")?.removeAttribute("role");
        };
        new MutationObserver(strip).observe(document, { childList: true, subtree: true });
      });
    },
  },

  "a11y-dialog-hidden-from-tree": {
    breaks:
      "the dialog's own controls' place in the accessibility tree, which is the CONTROL the "
      + "absence assertions are validated against — with it broken, 'the announcer is not in the "
      + "tree' could mean 'nothing is in the tree' and read the same",
    caughtBy:
      "a11y-live-regions.spec.ts — the offscreen announcers leave the accessibility tree "
      + "(specifically its control assertion)",
    apply: async (page) => {
      // The control had no mutant of its own for six review rounds, which is
      // the exact hazard it exists to prevent, applied to itself: if it stopped
      // discriminating, every other assertion in that test could false-pass and
      // the run would stay clean (codex round 3 on #504).
      //
      // `aria-hidden` on the dialog's BUTTONS, not on the dialog: hiding the
      // dialog itself would stop `getByRole("dialog")` matching and kill the
      // test at `toBeVisible()` — the wrong assertion, again.
      //
      // Honest about what it proves: that the control CAN fail, not that it
      // detects a broken CDP session specifically. Nothing here can simulate
      // that, and pretending otherwise would be the overclaim this file keeps
      // having to walk back.
      await page.addInitScript(() => {
        const hide = () => {
          for (const control of document.querySelectorAll('[role="dialog"] button')) {
            control.setAttribute("aria-hidden", "true");
          }
        };
        new MutationObserver(hide).observe(document, { childList: true, subtree: true });
      });
    },
  },

  "a11y-announcer-writes-late": {
    breaks:
      "the announcer by writing the warning ~500ms after the last dialog closes — a screen "
      + "reader still speaks it, long after any snapshot the test happens to take",
    caughtBy:
      "a11y-live-regions.spec.ts — a standing farm warning ... not re-announced (the final "
      + "cumulative assertion, and the only mutant that validates its 1200ms drain)",
    apply: async (page) => {
      // Validates a BOUND, which is the only honest way to have one. The
      // in-loop reads and the transient mutant both concern writes that land
      // promptly; nothing showed the final drain was long enough to catch a
      // slow one, so the drain was an unvalidated guess (codex round 1 on
      // #504).
      //
      // It fires only on the LAST close, and that is not arbitrary. A first
      // version wrote 500ms after EVERY close, and the harness's new
      // EXPECT_MSG_FOR check immediately reported it as killed at the wrong
      // assertion: cycle one's delayed write was still on screen when cycle
      // two's in-loop read happened, so it died there and left the final drain
      // just as unvalidated as before. Firing once, 600ms after the second
      // close, lands it after that read (250ms) and inside the drain (1200ms),
      // which is the only window where the cumulative assertion is the one
      // doing the work.
      //
      // The count is coupled to the spec running two cycles. If that changes,
      // this dies at the wrong assertion again and the harness says so — which
      // is the coupling being visible rather than silent.
      await page.addInitScript(() => {
        let wasInert = false;
        let closes = 0;
        const check = () => {
          const root = document.getElementById("root");
          const banner = document.querySelector("p.farm-warning");
          const region = document.querySelector('main.content > p.sr-only[aria-live="assertive"]');
          const nowInert = root?.hasAttribute("inert") ?? false;
          if (wasInert && !nowInert && banner && region && ++closes === 2) {
            const text = banner.textContent;
            setTimeout(() => { region.textContent = text; }, 600);
          }
          wasInert = nowInert;
        };
        new MutationObserver(check).observe(document, {
          attributes: true,
          childList: true,
          subtree: true,
          attributeFilter: ["inert"],
        });
      });
    },
  },

  // --- named entity picker (#512) -------------------------------------------
  "named-entity-picker-paging-broken": {
    breaks:
      "offset paging on the FlockPicker's discovery endpoint (#512) — every Load "
      + "more request past the first page is rewritten to offset 0, so the picker "
      + "re-serves the first 50 rows forever and the lexically-last page-two "
      + "sentinel (row 101 of 101) is never reached, the same shape as "
      + "stock-pager-inert but for the picker rather than the stock lot list",
    caughtBy:
      "named-entity-picker.spec.ts — Daily Entry's flock picker reaches and "
      + "commits the page-two sentinel through paging",
    apply: async (page) => {
      await page.route("**/api/v1/flocks**", async (route) => {
        const url = new URL(route.request().url());
        // Scoped to the PICKER's own discovery calls (they always carry
        // `eligibility`) and only to a genuine Load more request (`offset`
        // present and non-zero) — never the legacy `/flocks` list callers,
        // which pass neither.
        const offset = url.searchParams.get("offset");
        const eligibility = url.searchParams.get("eligibility");
        if (!eligibility || offset === null || offset === "0") return route.fallback();
        url.searchParams.set("offset", "0");
        const response = await route.fetch({ url: url.toString() });
        await route.fulfill({ response });
      });
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

  // --- preferences ---------------------------------------------------------
  "language-persist-dropped": {
    breaks: "server-side persistence of the language preference — the PUT answers 204 and saves nothing",
    caughtBy: "i18n.spec.ts — switching to <lang> renders that language across the shell",
    apply: async (page) => {
      // #486 — the durability half of that spec (clear the device hint, reload,
      // and expect the language to come back from /me) is the only thing
      // standing between "the server persisted it" and "the browser remembered
      // it". Nothing mutated that until now, which is the same blind spot that
      // let the spec's own persist assertion go vacuous without anyone noticing.
      //
      // Note what this does and does not cover: it breaks the SERVER guarantee,
      // so it goes red under the old spec as well as the new one. Whether the
      // spec waits for the RIGHT request is a property of the test, not of the
      // app, and cannot be mutated from the network boundary.
      await page.route("**/api/v1/me/language", async (route) => {
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
