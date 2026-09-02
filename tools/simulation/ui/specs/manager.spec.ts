// Manager persona — #277's flow: daily-entry submit -> review -> adjust/void,
// plus flock ops.
//
// ================== WHY THIS CREATES ITS OWN FLOCK ==================
//
// The obvious version of this spec submits today's entry on a seeded flock. It
// works exactly once. Submitting moves the entry out of Draft, so the SECOND run
// finds it locked, the fields disabled, and fails — and it fails in a way that
// reads like a permissions regression rather than like the spec having eaten its
// own fixture.
//
// The repo's own note is that the fixture is throwaway and `reset.sh` is
// authorised, so "just reseed between runs" is available. It is still the wrong
// default: a five-minute wipe as the price of re-running one spec means the spec
// gets run rarely, and a suite that is expensive to re-run is a suite whose red
// gets argued with instead of investigated.
//
// So this creates a flock, which covers #277's "flock ops" in the same pass, and
// then owns a brand-new Draft entry on it that no other spec and no previous run
// can have touched. Re-runnable against a dirty fixture, by construction.
//
// The cost, stated plainly: each run leaves one more flock and one voided entry
// behind. That is fixture growth, not fixture corruption — nothing else asserts
// on flock COUNT (the Owner dashboard asserts "not zero rows", deliberately), and
// `reset.sh` clears it whenever anyone wants a clean slate.

import type { Page } from "@playwright/test";
import { expect, test } from "../src/fixtures";
import { castMember } from "../src/cast";
import { commitNamedPicker } from "../src/dom";
import { farmCount, farmToday } from "../src/farm";
import { prefixOf, tEn } from "../src/i18n";

// Creates a flock through the UI and returns its id, captured from the POST
// response — the id is what the prefill synchronization below keys on.
async function createFlock(page: Page, flockName: string, today: string): Promise<string> {
  await page.goto("/flocks");
  await page.getByRole("button", { name: tEn("flocks:newFlockButton") }).click();
  const newFlock = page.getByRole("dialog", { name: tEn("flocks:newFlockDialogTitle") });
  await newFlock.getByLabel(tEn("flocks:nameLabel")).fill(flockName);
  await newFlock.getByLabel(tEn("flocks:breedLabel")).fill("E2E Leghorn");
  await newFlock.getByLabel(tEn("flocks:placedLabel")).fill(today);
  await newFlock.getByLabel(tEn("flocks:birdsLabel"), { exact: true }).fill("50");
  const created = page.waitForResponse((r) =>
    r.url().includes("/api/v1/flocks") && r.request().method() === "POST" && r.ok());
  await newFlock.getByRole("button", { name: tEn("flocks:addFlockButton") }).click();
  const flockId = ((await (await created).json()) as { id: string }).id;
  await expect(newFlock).toBeHidden();
  return flockId;
}

// #446 prefill: the Daily Entry page fires a prefill that RESETS every count
// when it settles, so fills must not race it. Two refuted designs, kept for
// the record: waiting for the enabled save button can observe the pre-effect
// render (`prefillPending` rises inside an effect — codex round 3), and
// registering waitForResponse only around the flock SELECT hangs when the
// just-created flock is already the page's auto-default, because selecting
// the same value fires no change and no new request (the CI red after
// e5771a0's first attempt). So the wait is registered BEFORE goto and keyed
// to the flock id: whichever path fires this flock's prefill — load-time
// auto-default, date change, or the select — is caught. The enabled
// assertion afterwards proves the settle was APPLIED: the response existing
// proves the effect ran and disabled the buttons, so enabled-after-response
// can only be the post-reset state.
async function openDailyEntryAwaitingPrefill(page: Page, flockId: string, flockName: string, today: string) {
  const prefill = page.waitForResponse((r) =>
    r.url().includes("/daily-entries") && r.url().includes(flockId) && r.ok());
  await page.goto("/daily-entry");
  await page.getByLabel(tEn("dailyEntry:dateLabel")).fill(today);
  // #512 — the flock field is a searchable picker, not a native <select>: the
  // flock this test just created is reached by NAME (server-side literal
  // search), not by scanning a capped option list, so the picker's own
  // discovery cap does not resurrect the old "past ~100 accumulated runs, the
  // newest flock is truncated away" hazard this comment used to warn about.
  const committedId = await commitNamedPicker(page, tEn("dailyEntry:flockLabel"), flockName);
  expect(committedId, "commitNamedPicker resolved a different flock id than the one this test created")
    .toBe(flockId);
  await prefill;
  await expect(page.getByRole("button", { name: tEn("dailyEntry:submitButton") })).toBeEnabled();
}

test.describe("Manager", () => {
  test("creates a flock, submits its entry, then adjusts and voids it", async ({
    page,
    signIn,
    farm,
  }) => {
    await signIn(castMember("Manager"));
    const today = farmToday(farm.timeZoneId);
    const flockName = `E2E Flock ${Date.now()}`;

    // ---- 1. Flock ops: create ----------------------------------------------
    const flockId = await createFlock(page, flockName, today);
    // The flock is on the farm's roster — the guarantee, not the dialog closing.
    await expect(page.getByRole("cell", { name: flockName })).toBeVisible();

    // ---- 2. Record and SUBMIT a daily entry on it --------------------------
    // Prefill-settle synchronization — see openDailyEntryAwaitingPrefill.
    // Without it the prefill wipes the fills and the spec submits an
    // all-zeros day (PR #464's CI-only 46.9s red, caught on video).
    await openDailyEntryAwaitingPrefill(page, flockId, flockName, today);
    await page.getByLabel(tEn("dailyEntry:totalEggsLabel"), { exact: true }).fill("40");
    await page.getByLabel(tEn("dailyEntry:crackedLabel"), { exact: true }).fill("1");
    // #394: submit is refused unless grading exactly reconciles sellable eggs
    // (40 total − 1 cracked = 39) — "Large" is a base default grade, present
    // on every install regardless of seed profile, so it's always offered here.
    await page.getByLabel("Large", { exact: true }).fill("39");

    await page.getByRole("button", { name: tEn("dailyEntry:submitButton") }).click();
    // Submitting is confirmed, not immediate. Stopping at the first click would
    // assert on a still-Draft entry and the rest of the spec would be testing
    // the wrong lifecycle state.
    await page
      .getByRole("dialog", { name: tEn("dailyEntry:confirmSubmitTitle") })
      .getByRole("button", { name: tEn("dailyEntry:confirmSubmitLabel") })
      .click();

    // THE STATE MOVED, and the way a user sees that is the screen going
    // read-only: DailyEntryPage sets `entryLocked` for any status past Draft and
    // shows the locked banner. Its text interpolates the status, so match the
    // stable prefix.
    await expect(
      page.getByText(prefixOf("en", "dailyEntry:entryLockedBanner")),
      "the entry did not leave Draft — submit did not take effect",
    ).toBeVisible();

    // ---- 3. Review it on History, then ADJUST ------------------------------
    await page.goto("/history");
    await commitNamedPicker(page, tEn("history:flockLabel"), flockName);

    const row = page.getByRole("row").filter({ hasText: flockName });
    await expect(row).toHaveCount(1);
    await expect(row).toContainText(tEn("history:statusSubmitted"));

    await row.getByRole("button", { name: tEn("history:adjustButton") }).click();

    // The adjust dialog's title interpolates the entry, so it is located by the
    // control only it has — the required reason field — rather than by a title
    // this spec would have to reconstruct.
    const adjustDialog = page
      .getByRole("dialog")
      .filter({ has: page.getByLabel(tEn("history:reasonLabel")) });
    // The dialog now mirrors the Daily entry form field for field, so its count
    // labels come from THAT screen's namespace (`dailyEntry`), not `history`.
    await adjustDialog.getByLabel(tEn("dailyEntry:totalEggsLabel"), { exact: true }).fill("38");
    // #394: an adjustment has no draft state — Save stays disabled unless
    // grading reconciles exactly. Cracked carries over at 1, so 38 − 1 = 37.
    await adjustDialog.getByLabel("Large", { exact: true }).fill("37");
    await adjustDialog.getByLabel(tEn("history:reasonLabel")).fill("E2E recount");
    await adjustDialog.getByRole("button", { name: tEn("history:saveAdjustmentButton") }).click();

    await expect(adjustDialog).toBeHidden();
    // HistoryPage announces success in a role="status" region — the same thing
    // the operator is told, and screen-reader users are told.
    //
    // Narrowed to the PARAGRAPH. A bare getByRole("status") is ambiguous here
    // and always will be: every BusyButton renders its own sr-only
    // `<span role="status">` sibling for the "Working…" announcement, so any row
    // carrying an action button contributes one. Matching `p[role="status"]`
    // keeps the accessibility meaning (this is the live announcement, not just
    // some green text) while excluding those.
    const announcement = page.locator('p[role="status"]');
    await expect(announcement).toContainText(tEn("history:entryAdjustedMessage"));
    // And the row now reads Adjusted. The message alone would pass against an
    // announcement fired on a write that did not land.
    await expect(row).toContainText(tEn("history:statusAdjusted"));

    // ---- 4. VOID it --------------------------------------------------------
    await row.getByRole("button", { name: tEn("history:voidButton") }).click();

    // Voiding goes through askReason(), whose title interpolates the date and
    // flock — located, again, by the confirm control rather than by the title.
    const voidDialog = page
      .getByRole("dialog")
      .filter({ has: page.getByRole("button", { name: tEn("history:voidConfirmLabel") }) });
    await voidDialog.getByRole("textbox").fill("E2E void");
    await voidDialog.getByRole("button", { name: tEn("history:voidConfirmLabel") }).click();

    await expect(announcement).toContainText(tEn("history:entryVoidedMessage"));
    await expect(row).toContainText(tEn("history:statusVoided"));
  });

  // #406 — the corrective tier's newest verb: a standalone per-lot write-off.
  // Same self-owned-fixture doctrine as the flow above: this test creates its
  // own flock and entry, so the lot it writes off is one no other spec and no
  // previous run can have touched. The egg count is unique-ish per run so the
  // lot row can be located unambiguously against a dirty fixture.
  test("writes off lost stock from its own lot without touching the entry", async ({
    page,
    signIn,
    farm,
  }) => {
    await signIn(castMember("Manager"));
    const today = farmToday(farm.timeZoneId);
    const flockName = `E2E WriteOff Flock ${Date.now()}`;
    // The produced count is this run's HANDLE on its own lot, so it has to be
    // unlikely to repeat: 200–9999, disjoint from the 38–40 the flow spec uses
    // and from the 41–98 older fixtures are full of. `TotalEggs` has no upper
    // bound in RecordDailyEntryValidator, so a four-digit day is accepted.
    //
    // It was 41 + (Date.now() % 58) until #506 — 58 values, one new same-day
    // lot per run, so a collision arrived within a few runs and the spec then
    // acted on an earlier run's lot. Widening is not a proof of uniqueness, so
    // the lookup below ASSERTS there is exactly one match rather than taking
    // the first.
    const eggs = 200 + (Date.now() % 9800);

    const flockId = await createFlock(page, flockName, today);

    await openDailyEntryAwaitingPrefill(page, flockId, flockName, today);
    await page.getByLabel(tEn("dailyEntry:totalEggsLabel"), { exact: true }).fill(String(eggs));
    await page.getByLabel("Large", { exact: true }).fill(String(eggs));
    // The fills survived to the submit. #464's prefill can wipe them, and a
    // wiped form submits an all-zeros day whose banner looks identical.
    await expect(page.getByLabel(tEn("dailyEntry:totalEggsLabel"), { exact: true }))
      .toHaveValue(String(eggs));
    await expect(page.getByLabel("Large", { exact: true })).toHaveValue(String(eggs));
    await page.getByRole("button", { name: tEn("dailyEntry:submitButton") }).click();
    await page
      .getByRole("dialog", { name: tEn("dailyEntry:confirmSubmitTitle") })
      .getByRole("button", { name: tEn("dailyEntry:confirmSubmitLabel") })
      .click();
    await expect(page.getByText(prefixOf("en", "dailyEntry:entryLockedBanner"))).toBeVisible();
    // The lot EXISTS, pinned by the created-count in the success message — a
    // wiped form submits an all-zeros day whose banner looks identical but
    // creates no lot, and the spec would then time out far away on /stock.
    await expect(page.getByText(
      tEn("dailyEntry:submittedMessage").replace("{{count}}", "1"))).toBeVisible();

    // ---- The write-off itself ---------------------------------------------
    //
    // Record the size of every lot page the SERVER sends. This is the only
    // unambiguous end-of-list signal available: the pager button is rendered
    // `{hasMoreLots && !lotsLoading}`, so its absence means either "no more
    // pages" or "a fetch is in flight", and two bugs in this spec came from
    // reading it in the second state as though it were the first. Waiting for
    // the response did not fix that either — `waitForResponse` resolves before
    // `loadMoreLots` has parsed the body and React has committed
    // `setLotsLoading(false)` (codex rounds 8 and 9). A short page, by
    // contrast, can only mean the list is exhausted.
    // Mirrors LOT_PAGE in web/src/routes/StockPage.tsx:26. Asserted EXACTLY
    // against the `limit` each request actually sends, so a change in either
    // direction fails here with a message. An earlier version asserted only
    // `<= LOT_PAGE`, which a lowered page size satisfies silently — a full
    // 25-row page would then read as a short final page (codex round 11).
    const LOT_PAGE = 50;

    // Every lot page the server sent, KEYED TO ITS REQUEST. Position alone is
    // not enough: `from.fill(today)` starts a request against the *previous*
    // window, and if that body parses after a positional mark its empty page
    // is attributed to the final reload and read as end-of-list. The window a
    // page belongs to is in its query string, so that is what identifies it.
    interface LotPage { from: string; to: string; offset: number; limit: number; size: number }
    const lotPages: LotPage[] = [];
    page.on("response", (response) => {
      const url = new URL(response.url());
      if (!url.pathname.endsWith("/stock/lots")) return;
      if (response.request().method() !== "GET" || !response.ok()) return;
      const from = url.searchParams.get("from") ?? "";
      const to = url.searchParams.get("to") ?? "";
      const offset = Number(url.searchParams.get("offset") ?? "0");
      const limit = Number(url.searchParams.get("limit") ?? "0");
      void response
        .json()
        .then((body: unknown) => {
          const items = Array.isArray(body)
            ? body
            : (body as { items?: unknown[] } | null)?.items;
          if (Array.isArray(items)) lotPages.push({ from, to, offset, limit, size: items.length });
        })
        .catch(() => {
          /* an unreadable body tells us nothing; the polls below wait */
        });
    });

    await page.goto("/stock");
    // No date bounds until the filter section at the end.
    const WHOLE_HISTORY = { from: "", to: "" };
    const openedLots = lotPages.length;
    await page
      .getByRole("row")
      .filter({ has: page.getByRole("cell", { name: "Large", exact: true }) })
      .getByRole("button", { name: tEn("stock:lotsButton") })
      .click();

    // #650 — the visible date is the farm's own format (locale short form or
    // its Settings override), so the row is picked by the ISO day the cell's
    // <time datetime> carries, never by the rendered label.
    const todayRows = page
      .getByRole("row")
      .filter({ has: page.locator(`time[datetime="${today}"]`) });

    // Produced (td 2) and available (td 3) for every same-day row, read
    // positionally. `filter({ has: cell })` cannot express this: two clauses
    // naming the same text are satisfied by the SAME cell, so while produced
    // equals available it collapses into "a row containing that number
    // anywhere" — which once made this spec write off a stranger's lot.
    const balances = async () => todayRows.evaluateAll((rows) =>
      rows.map((row) => {
        const cells = row.querySelectorAll("td");
        return [cells[1]?.textContent?.trim() ?? "", cells[2]?.textContent?.trim() ?? ""];
      }));

    // IDENTITY IS THE PRODUCED COUNT, asserted unique — not a row position and
    // not a balance. Position cannot be carried: every refetch (the write-off,
    // and the date-filter round trip below) resets the list to page one, so an
    // index captured earlier points somewhere else or nowhere. Balances cannot
    // identify either: an abandoned earlier run leaves an untouched twin and a
    // completed one leaves a written-off twin, both indistinguishable from ours
    // by balance alone (codex rounds 6 and 7).
    //
    // So the row is re-resolved whenever it is needed, and the resolution is
    // only allowed to succeed if exactly one same-day lot carries this run's
    // produced count.
    // `mark` is the recorder's length taken BEFORE the action that reloads the
    // list. Every call must pass one: the recorder accumulates for the whole
    // test, so without it the poll below is satisfied by pages from an EARLIER
    // walk and the loop can read a stale short page as end-of-list before this
    // reload has even responded (codex round 10).
    const ourLotIndex = async (
      mark: number,
      window: { from: string; to: string },
    ): Promise<number> => {
      // Page to the end. The loop is driven by the SERVER's page sizes, never
      // by the pager button: while the last page came back full there is more
      // to fetch, and a short page means the list is exhausted. The button is
      // still what gets clicked, but it is no longer what decides.
      await expect(page.getByRole("heading", { name: tEn("stock:lotsHeading") })).toBeVisible();
      await expect(
        page.getByRole("columnheader", { name: tEn("stock:producedOnHeader") }),
      ).toBeVisible();
      // Pages from THIS reload and THIS window. The mark excludes earlier
      // walks; the window excludes a request the previous filter value started,
      // whose body can land after the mark and would otherwise be read as this
      // reload's end-of-list.
      const pages = () =>
        lotPages.slice(mark).filter((p) => p.from === window.from && p.to === window.to);
      await expect.poll(() => pages().length).toBeGreaterThan(0);

      // The mirrored page size, checked against what the app actually asked
      // for — exactly, in both directions.
      expect(
        pages()[0]!.limit,
        `StockPage requested limit=${pages()[0]!.limit} but this spec mirrors LOT_PAGE=${LOT_PAGE}; `
          + `its end-of-list test is now wrong in one direction or the other`,
      ).toBe(LOT_PAGE);

      // A short page is the end of the list, and only a short page is — the
      // same rule StockPage itself applies as `hasMoreLots = page.length ===
      // LOT_PAGE`. The last page of THIS walk is the one with the highest
      // offset, not the last recorded, because responses can settle out of
      // order.
      const latest = () => pages().reduce((a, b) => (b.offset > a.offset ? b : a));

      // The cap is a runaway guard, NOT an exit. Falling out of it means the
      // list was never paged to the end, so a duplicate produced count could be
      // sitting on the page after the last one fetched and the uniqueness check
      // below would call identity proven anyway (codex round 13). Whether the
      // walk finished is therefore asserted, not assumed.
      // The terminal page is inspected AFTER every fetch, including the last one
      // the cap allows. Checking only before each fetch meant a 25th click that
      // returned the short page exited the loop without ever looking at it, and
      // a fully-traversed list then failed claiming it had not been traversed
      // (codex round 14 — a false failure introduced by round 13's guard).
      const PAGE_CAP = 25;
      let reachedEnd = latest().size < LOT_PAGE;
      for (let i = 0; i < PAGE_CAP && !reachedEnd; i++) {
        const seen = pages().length;

        const more = page.getByRole("button", { name: tEn("stock:loadMoreButton") });
        // It reappears once the previous fetch commits; a genuine end-of-list
        // was already caught above, so a timeout here is a real failure rather
        // than a normal exit.
        await more.waitFor({ state: "visible" });
        await more.click();
        await expect.poll(() => pages().length).toBeGreaterThan(seen);
        reachedEnd = latest().size < LOT_PAGE;
      }

      expect(
        reachedEnd,
        `paged ${PAGE_CAP} times without reaching a short page — this window holds more than `
          + `${PAGE_CAP * LOT_PAGE} lots, so the uniqueness check below cannot see them all and `
          + `identity is not proven. Run reset.sh, or raise the cap deliberately.`,
      ).toBe(true);

      // EVERY FETCHED PAGE MUST BE ON SCREEN before identity is judged. The
      // recorder observes a response when its body parses, which is before
      // `loadMoreLots` commits the rows to React — so paging can exit on the
      // terminal short page while that page is still uncommitted. The
      // uniqueness poll below would then see one matching row, pass, and act on
      // a lot whose twin simply had not rendered yet (codex round 12).
      //
      // Offsets within a walk are disjoint and this run adds no lots mid-walk
      // (one worker, its own lot created before the list was opened), so the
      // rendered count must equal the sum of the pages fetched. StockPage
      // dedupes by id on append, so a SHORTFALL means offsets drifted — worth
      // failing on rather than waiting out.
      // A lot row is one whose first cell is a date — read from the <time
      // datetime> the cell carries (#650), never from the label, which is the
      // farm's own date format.
      const lotRowsOnScreen = async () =>
        page.getByRole("row").evaluateAll((rows) =>
          rows.filter((row) => {
            const first = row.querySelector("td:first-child time[datetime]");
            return first !== null && /^\d{4}-\d{2}-\d{2}$/.test(first.getAttribute("datetime") ?? "");
          }).length);

      const fetched = pages().reduce((total, p) => total + p.size, 0);
      await expect
        .poll(lotRowsOnScreen, {
          message:
            `${fetched} lot rows were fetched for this window but the table never showed that `
            + `many; if it settled lower, offset paging re-served rows and the dedupe dropped them`,
        })
        .toBe(fetched);

      // `evaluateAll` does not auto-wait, unlike an assertion on a locator, so
      // this polls rather than reading once.
      let at = -1;
      await expect
        .poll(async () => {
          const rows = await balances();
          const hits = rows
            .map(([produced], index) => (produced === farmCount(eggs, farm.locale) ? index : -1))
            .filter((index) => index >= 0);
          at = hits.length === 1 ? hits[0]! : -1;
          return hits.length;
        }, {
          message:
            `expected exactly one same-day lot with ${eggs} produced. Zero means the entry above `
            + `created no lot; more than one means this run's produced count collided with `
            + `another run's and the spec cannot tell which lot is its own`,
        })
        .toBe(1);
      return at;
    };

    const index = await ourLotIndex(openedLots, WHOLE_HISTORY);
    const lotRow = todayRows.nth(index);
    await lotRow.getByRole("button", { name: tEn("stock:writeOffButton") }).click();

    const writeOff = page
      .getByRole("dialog")
      .filter({ has: page.getByRole("button", { name: tEn("stock:writeOffSubmitButton") }) });
    await writeOff.getByLabel(tEn("stock:writeOffQuantityLabel"), { exact: true }).fill("2");
    await writeOff.getByLabel(tEn("stock:writeOffReasonLabel")).fill("E2E cooler breakage");
    const submittedWriteOff = lotPages.length;
    await writeOff.getByRole("button", { name: tEn("stock:writeOffSubmitButton") }).click();

    await expect(writeOff).toBeHidden();
    const announcement = page.locator('p[role="status"]');
    await expect(announcement).toContainText(prefixOf("en", "stock:writeOffRecordedMessage"));
    // THE SAME LOT, re-resolved — the write-off refetched the list and reset it
    // to page one, so the earlier index no longer points at anything reliable.
    // Produced is unchanged, which is the point: the write-off moved the
    // balance without restating the day's laying.
    const afterWriteOff = await ourLotIndex(submittedWriteOff, WHOLE_HISTORY);
    expect((await balances())[afterWriteOff]).toEqual([farmCount(eggs, farm.locale), farmCount(eggs - 2, farm.locale)]);

    // ---- #465: the date filter reaches lots server-side -------------------
    // A window that cannot contain this lot empties the table…
    await page.getByLabel(tEn("stock:fromLabel"), { exact: true }).fill("2000-01-01");
    await page.getByLabel(tEn("stock:toLabel"), { exact: true }).fill("2000-01-02");
    await expect(page.getByText(tEn("stock:noLotsMessage"))).toBeVisible();
    // …and narrowing to exactly today brings it back, corrected balance intact.
    await page.getByLabel(tEn("stock:fromLabel"), { exact: true }).fill(today);
    // Marked before the LAST fill, and matched on the window itself: each fill
    // starts its own request, and the intermediate one (from=today with the
    // old `to`) can settle after this mark.
    const narrowedToToday = lotPages.length;
    await page.getByLabel(tEn("stock:toLabel"), { exact: true }).fill(today);
    // Re-resolved again: changing the window refetches from offset zero.
    const afterFilter = await ourLotIndex(narrowedToToday, { from: today, to: today });
    expect(
      (await balances())[afterFilter],
      "narrowing the window to today did not bring back the corrected lot",
    ).toEqual([farmCount(eggs, farm.locale), farmCount(eggs - 2, farm.locale)]);
  });

  test("can reach the admin destinations a Worker cannot", async ({ signIn, nav }) => {
    // The mirror of the Worker spec's gate assertion. Manager is isAdmin, so the
    // Setup group is present — EXCEPT Users, which nav.tsx narrows to role
    // "Admin" alone. That one exclusion is the interesting part: it is the
    // difference between "isAdmin" and "Admin", and it is easy to regress into
    // showing a Manager the user-management screen.
    await signIn(castMember("Manager"));
    await expect(nav.link("nav:audit")).toBeVisible();
    await expect(nav.link("nav:export")).toBeVisible();
    await expect(nav.link("nav:expenses")).toBeVisible();
    await expect(nav.link("nav:farmSettings")).toBeVisible();
    await expect(
      nav.link("nav:users"),
      "the sidebar offered Users to a Manager — nav.tsx gates that on role === 'Admin'",
    ).toBeHidden();
  });
});
