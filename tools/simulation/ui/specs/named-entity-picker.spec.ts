// #512 — the searchable paged entity picker (FlockPicker/CustomerPicker),
// over the unchanged #627 fixture.
//
// ================== WHY THESE TWO SENTINELS, AGAIN ==================
//
// pagination.spec.ts already proves the #627 sentinels are reachable through
// each surface's plain LIST (customers table, bird-movement ledger, feed
// ledger). This spec proves the SAME two named sentinels — the flock catalog's
// "Sim Z Flock Page Two" and the customer book's "Sim Customer Z Page Two" —
// are also reachable through the picker introduced by #512, which a real user
// meets on Daily Entry (flock) and on a new Sales order (customer). Nothing
// here changes the fixture, the seeder, or its counts; SimulationDataSeeder
// and SimulationSeederTests remain the source of truth for the exact bands.
//
// ================== THE THREE CONTRACTS COVERED ==================
//
// 1. Unfiltered PAGING reaches the flock sentinel: with eligibility
//    active-and-depleted the picker sees 101 non-archived flocks (comment at
//    SimulationDataSeeder.cs ~L824), and its 50-row pages put the
//    lexically-last sentinel on the THIRD page (50 + 50 + 1) — two Load more
//    clicks, not one. This is also the test the paging mutant
//    (named-entity-picker-paging-broken, mutants.ts) must turn red.
// 2. SEARCH reaches the customer sentinel: typing a substring unique to the
//    sentinel name narrows the 101-row customer book to one match, without
//    ever paging.
// 3. RECOVERY: one aborted discovery request puts the customer picker into
//    its replacement-error state (translated "Search failed" + Retry);
//    Retry recovers it, and the same search-then-commit as (2) completes.
//
// Nothing here writes: opening the New order dialog and picking a customer is
// pure client state — the order is never submitted, so this spec is
// re-runnable against a dirty fixture like pagination.spec.ts.

import { expect, test } from "../src/fixtures";
import { castMember } from "../src/cast";
import { tEn } from "../src/i18n";

const FLOCK_SENTINEL = "Sim Z Flock Page Two";
const CUSTOMER_SENTINEL = "Sim Customer Z Page Two";

test.describe("Searchable named-entity picker (#512)", () => {
  test("Daily Entry's flock picker reaches and commits the page-two sentinel through paging", async ({
    page,
    signIn,
    nav,
  }) => {
    await signIn(castMember("Manager"));
    await nav.link("nav:dailyEntry").click();

    // The trigger's accessible name is "<label> <current value>" (aria-labelledby),
    // and the current value may already be a remembered/default flock rather than
    // the empty "Select a flock" placeholder — match on the stable label prefix.
    await page.getByRole("button", { name: new RegExp(`^${tEn("dailyEntry:flockLabel")} `) }).click();
    const combobox = page.getByRole("combobox", { name: tEn("dailyEntry:flockLabel") });
    await expect(combobox).toBeVisible();

    // Absent before: the #627 fixture alone puts 101 non-archived flocks over
    // 50-row pages, so the lexically-last sentinel starts on at least the
    // THIRD page — not reachable from the first two without paging. (Other
    // specs in this suite create their own throwaway flocks and never delete
    // them — see manager.spec.ts's own header — so the true count only grows
    // from there; nothing below assumes an exact total.)
    const sentinel = page.getByRole("option", { name: FLOCK_SENTINEL });
    await expect(sentinel).toHaveCount(0);

    // KEYBOARD paging, deliberately not the mouse: the Keyboard Contract
    // (contracts/picker-ui.md) specifies "Down Arrow past the loaded end
    // with more results requests extension", and this is the one path this
    // spec can drive without landing a click on the popup listbox itself —
    // it is a floating `position: absolute` overlay (.named-picker-listbox,
    // z-index 30) that visually sits on top of the Load more button
    // beneath it once the list is long enough to need its own scrollbar, so
    // a literal mouse click there is not currently reachable in a real
    // browser. Filed for the picker owners; not this task's file to fix.
    // Each ArrowDown is a no-op once a request is in flight (the engine's
    // own `phase === "ready"` guard), so pressing well past the true count
    // is safe — this loop simply stops the moment the sentinel is loaded.
    // Bounded generously above the fixture's 101 to absorb flocks other specs
    // created earlier in a shared, un-reset run.
    for (let i = 0; i < 260 && (await sentinel.count()) === 0; i++) {
      await combobox.press("ArrowDown");
    }
    // Present after paging via the keyboard to the loaded end, with the
    // fixture's exact name — the assertion the paging mutant
    // (named-entity-picker-paging-broken) must turn red.
    await expect(
      sentinel,
      "the flock page-two sentinel never appeared after keyboard-paging to the loaded end",
    ).toHaveCount(1);

    // The sentinel — lexically last among "Sim Z Flock …" names — is not
    // necessarily the ONLY row on its final page once other specs have added
    // flocks of their own, so keep arrowing (never assume exactly one more
    // press) until it is genuinely the active option, then commit with
    // Enter — also part of the Keyboard Contract, and never a click.
    for (let i = 0; i < 60; i++) {
      const activeClass = await sentinel.getAttribute("class");
      if (activeClass?.includes("active")) break;
      await combobox.press("ArrowDown");
    }
    await expect(sentinel).toHaveClass(/active/);
    await combobox.press("Enter");
    await expect(combobox).toHaveValue(FLOCK_SENTINEL);
  });

  test("a new Sales order's customer picker reaches and commits the page-two sentinel through search", async ({
    page,
    signIn,
    nav,
  }) => {
    await signIn(castMember("Manager"));
    await nav.link("nav:sales").click();
    await page.getByRole("button", { name: tEn("sales:newOrder") }).click();

    // Scoped to the dialog: the Sales list's own Customer FILTER control
    // (a native <select>, closed by default) shares the same accessible
    // name ("Customer") and would otherwise make this locator ambiguous.
    const dialog = page.getByRole("dialog", { name: tEn("sales:newOrder") });
    const combobox = dialog.getByRole("combobox", { name: tEn("sales:customer") });
    await expect(combobox).toBeVisible();

    // Absent before: the picker opens on the unfiltered first page (up to 50
    // of 101 customers), and the sentinel — lexically last — is not on it.
    const sentinel = page.getByRole("option", { name: CUSTOMER_SENTINEL });
    await expect(sentinel).toHaveCount(0);

    // A literal substring unique to the sentinel's name narrows the whole
    // 101-row book to one match — reachable by search alone, no paging.
    await combobox.fill("Page Two");
    await expect(sentinel, "search for \"Page Two\" never surfaced the customer page-two sentinel").toHaveCount(1);

    await sentinel.click();
    await expect(dialog.getByRole("button", {
      name: `${tEn("sales:customer")} ${CUSTOMER_SENTINEL}`,
      exact: true,
    })).toBeVisible();
  });

  test("recovers from a failed customer search with Retry, then reaches the sentinel", async ({
    page,
    signIn,
    nav,
  }) => {
    await signIn(castMember("Manager"));

    // Abort exactly the FIRST picker discovery request (the fixed 50-row
    // adapter page) and let every later one through — a real, one-shot
    // network failure, not the CLUCKWORK_E2E_MUTANT harness (which is for
    // proving mutation coverage, not for driving an ordinary spec).
    let aborted = false;
    await page.route("**/api/v1/customers**", async (route) => {
      const url = new URL(route.request().url());
      if (!aborted && url.searchParams.get("limit") === "50") {
        aborted = true;
        await route.abort();
        return;
      }
      await route.fallback();
    });

    await nav.link("nav:sales").click();
    await page.getByRole("button", { name: tEn("sales:newOrder") }).click();

    // Scoped to the dialog: the Sales list's own Customer FILTER control
    // (a native <select>, closed by default) shares the same accessible
    // name ("Customer") and would otherwise make this locator ambiguous.
    const dialog = page.getByRole("dialog", { name: tEn("sales:newOrder") });
    const combobox = dialog.getByRole("combobox", { name: tEn("sales:customer") });
    await expect(combobox).toBeVisible();

    // The picker's own replacement-error state: translated "Search failed"
    // plus an adjacent, keyboard-reachable Retry — never a raw error, never a
    // silent empty list.
    // role="alert" does not compute its accessible name from content (ARIA
    // accname: "name from author", not content) — match role + text
    // separately, the same way the rest of this suite asserts on alerts
    // (`getByRole("alert")` alone, e.g. owner.spec.ts/readonly.spec.ts).
    const searchFailedAlert = page.getByRole("alert").filter({ hasText: tEn("namedEntityPicker:searchFailed") });
    await expect(searchFailedAlert).toBeVisible();
    const retry = page.getByRole("button", { name: tEn("namedEntityPicker:retry") });
    await expect(retry).toBeVisible();

    await retry.click();
    await expect(searchFailedAlert).toHaveCount(0);

    // Recovered: the same search-then-commit path as the previous test now
    // succeeds through the retried discovery.
    const sentinel = page.getByRole("option", { name: CUSTOMER_SENTINEL });
    await combobox.fill("Page Two");
    await expect(sentinel).toHaveCount(1);

    await sentinel.click();
    await expect(dialog.getByRole("button", {
      name: `${tEn("sales:customer")} ${CUSTOMER_SENTINEL}`,
      exact: true,
    })).toBeVisible();
  });
});
