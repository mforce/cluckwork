// #627 — over-cap pagination. The simulation fixture now holds MORE than one
// page on the three paged surfaces a farm user actually walks, and each
// surface's second page carries a NAMED sentinel row the seeder leaves as the
// band's identity:
//
//   customers                101 rows  (CUSTOMER_PAGE = 100)  -> page two holds 1
//   bird-movement ledger     69 rows on Sim House A
//                          (LEDGER_PAGE = 50)              -> page two holds 19
//   feed item's ledger       120 rows  (LEDGER_PAGE = 100)   -> page two holds 20
//
// (Counts are the DEFAULT 90-day history fixture: 18 automatic mortality +
// 51 explicit adjustments on House A; the feed item carries exactly 120
// movements while the account-wide total is 122 — the other 2 live on the
// bedding item. The seeder's completion check certifies all of these exactly.)
//
// The sentinel's POSITION in its list is a detail the spec does not claim.
// The only positional claim in each test is presence after exactly one click:
//   customers: the sentinel is the alphabetically-last name — end of list.
//   bird ledger: the band walks day offsets 1..63 (the first 51 not
//                divisible by five) and the sentinel rides the LAST offset
//                (63) — the OLDEST explicit adjustment. The automatic
//                mortality rows STRADDLE it: 12 are newer (offsets 5..60), 6
//                are older (offsets 65..90). It sits on page two of the
//                date-descending ledger because 62 rows (the 12 newer
//                mortality + the 50 newer explicit at offsets 1..62 excluding
//                multiples of five) are newer, not because every mortality
//                row is older.
//   feed ledger: the sentinel is the oldest ADDED adjustment, but the opening
//                purchase is older than that.
//
// ================== THE CONTRACT: ABSENT BEFORE, ONE CLICK, PRESENT AFTER ==================
//
// Each test proves the sentinel is NOT on the first page, clicks its surface's
// translated Load-more control EXACTLY ONCE, then proves the sentinel row is
// there with its fixture detail. The absence-before-click assertion is
// load-bearing: a sentinel that renders on page one means the page size, the
// ordering, or the seeder's count drifted, and a one-click spec would never
// find out because it would pass on the wrong page. No looping: with the
// certified counts one click is sufficient, so a missing sentinel after one
// click is an ordering bug to report, not something to wait out.
//
// Nothing here writes: the fixture is deterministic and the seeder's
// ValidateCounts throws unless it holds exactly these bands, so the rows are
// re-runnable by construction (a dirty fixture is a reset.sh problem, not a
// spec problem — manager.spec.ts owns the self-owned-fixture doctrine for the
// specs that DO write).

import { expect, test } from "../src/fixtures";
import { castMember } from "../src/cast";
import { tEn } from "../src/i18n";

test.describe("Over-cap pagination (#627)", () => {
  test("the customer book's page two holds the sentinel customer", async ({ page, signIn, nav }) => {
    await signIn(castMember("Manager"));
    await nav.link("nav:customers").click();

    // Absent before: 101 customers over CUSTOMER_PAGE=100 means the
    // alphabetically-last sentinel cannot be on the first 100.
    const sentinel = page.getByRole("row").filter({ hasText: "Sim Customer Z Page Two" });
    await expect(sentinel).toHaveCount(0);

    const loadMore = page.getByRole("button", { name: tEn("customers:loadMoreButton") });
    await expect(loadMore).toBeVisible();
    await loadMore.click();

    // Present after, with the fixture detail: the seeder gives the sentinel
    // phone 555-0299 and note "Simulation fixture customer" — data literals,
    // not UI copy.
    await expect(sentinel).toHaveCount(1);
    await expect(sentinel.getByRole("cell", { name: "555-0299" })).toBeVisible();
    await expect(sentinel.getByRole("cell", { name: "Simulation fixture customer" })).toBeVisible();
  });

  test("Sim House A's bird-movement ledger reveals its page-two sentinel", async ({ page, signIn, nav }) => {
    await signIn(castMember("Manager"));
    await nav.link("nav:flocks").click();

    const houseA = page.getByRole("row").filter({ hasText: "Sim House A" });
    await expect(houseA).toHaveCount(1);
    await houseA.getByRole("button", { name: tEn("flocks:openLedgerButton") }).click();

    // Absent before: the ledger opens with its first 50 of 69 rows (newest
    // first), and the sentinel — the oldest explicit adjustment, offset 63 —
    // is not among them: 62 rows are newer (12 automatic mortality at offsets
    // 5..60 plus the 50 explicit rows at offsets 1..62 excluding multiples of
    // five). Proven anyway because that is the point of the spec.
    const sentinel = page.getByRole("row").filter({ hasText: "Sim bird movement page two sentinel" });
    await expect(sentinel).toHaveCount(0);

    const loadMore = page.getByRole("button", { name: tEn("flocks:loadMoreButton") });
    await expect(loadMore).toBeVisible();
    await loadMore.click();

    // Present after, with the fixture detail: every explicit row is a +1
    // Adjustment. The quantity cell renders the domain's positive quantity in
    // the flock ledger's Unicode minus form ("−1"), and the type cell reads
    // the translated Adjustment label (never a raw enum string).
    await expect(sentinel).toHaveCount(1);
    await expect(sentinel.getByRole("cell", { name: "−1" })).toBeVisible();
    await expect(sentinel.getByRole("cell", { name: tEn("enums:flockMovement.Adjustment") })).toBeVisible();
  });

  test("the feed item's movement ledger reveals its page-two sentinel", async ({ page, signIn, nav }) => {
    await signIn(castMember("Manager"));
    await nav.link("nav:inventory").click();

    const feedRow = page.getByRole("row").filter({ hasText: "Sim Layer Feed" });
    await expect(feedRow).toHaveCount(1);
    await feedRow.getByRole("button", { name: tEn("inventory:openButton") }).click();

    // Absent before: the ledger opens with its first 100 of 120 rows, and the
    // sentinel — the oldest ADDED adjustment, though the opening purchase is
    // older than that — is not among them. Proven anyway because that is the
    // point of the spec.
    const sentinel = page.getByRole("row").filter({ hasText: "Sim feed adjustment page two sentinel" });
    await expect(sentinel).toHaveCount(0);

    const loadMore = page.getByRole("button", { name: tEn("inventory:loadMoreButton") });
    await expect(loadMore).toBeVisible();
    await loadMore.click();

    // Present after, with the fixture detail: every band row is a +1
    // Adjustment on the feed lot — the quantity cell renders "+1 kg" and the
    // type cell reads the translated Adjustment label (never a raw enum).
    await expect(sentinel).toHaveCount(1);
    await expect(sentinel.getByRole("cell", { name: "+1 kg" })).toBeVisible();
    // The type cell is the row's SECOND cell — named-role matching would
    // also hit the note cell, whose text CONTAINS "adjustment" (the sentinel
    // reason), and Playwright's name match is case-insensitive.
    await expect(sentinel.locator("td").nth(1)).toHaveText(tEn("enums:inventoryMovement.Adjustment"));
  });
});
