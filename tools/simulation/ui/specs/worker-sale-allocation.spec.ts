// Restricted-Worker sale confirmation — #612's privacy fix, end to end in a
// real browser.
//
// ================== WHY THIS TEST DOES NOT ASSERT A FIXED OUTCOME ==================
//
// sales.spec.ts (the Sales persona) proves the happy path end to end. It
// deliberately picks a small quantity because it needs the confirm to
// SUCCEED. A restricted Worker's confirm has a second axis #277 has never
// exercised — the design's #612 "Required proof" list — the FIFO/lock
// mechanics are already covered at the integration level
// (SaleAllocationPolicyTests.cs); what a real browser adds is that the SPA
// never renders farm-wide grade/quantity/flock detail to a restricted Worker,
// no matter which of the two outcomes the seeded stock happens to produce on
// a given run:
//
//   enough stock (assigned flock, or farm-wide under AllFarmFlocks) -> 200, Confirmed
//   not enough                                                      -> 422, ONE of the
//     two generic messages (never the detailed "Insufficient stock for grade
//     'X': N eggs unallocated." a non-restricted caller would see)
//
// So this spec orders an ABSURDLY large quantity — the seed can never cover
// it — to make the insufficient-stock path the overwhelmingly likely one
// without hardcoding a stock count that would drift as the fixture grows
// (HistoryDays, re-runs), and asserts whichever of the two generic outcomes
// actually happened, while asserting the ONE thing that must always be true
// regardless: no grade name and no leaked remaining count.
//
// It does NOT change WorkerSaleAllocationPolicy — that farm setting is
// GLOBAL, and mutating it here would race every other spec that assumes the
// fixture's current policy (sales.spec.ts's Sales-role confirm is unaffected
// by the setting, but a parallel run of THIS spec against a just-flipped
// policy would not be testing what it says it's testing). The default
// (AssignedFlocksOnly) is what SimulationDataSeeder ships, and is enough to
// prove the fix: the buggy code path this fix closes (AllFarmFlocks entering
// the detailed branch) is unreachable when it's exercised through the OTHER
// branch (AssignedFlocksOnly's own farm-wide retry) already, and is proven at
// the integration-test level (SaleAllocationPolicyTests.cs) with the
// deterministic control a fixed policy switch needs, which this suite cannot
// give it without the race above.

import { expect, test } from "../src/fixtures";
import { restrictedWorker } from "../src/cast";
import { farmToday } from "../src/farm";
import { tEn } from "../src/i18n";
import { selectOptionContaining } from "../src/dom";

const PRODUCT = "Sim Large Eggs";
// No fixture stock — seeded, growing, or drawn down by other specs — could
// ever reach this. The point is to make "insufficient" deterministic without
// pinning to a specific seeded quantity.
const ABSURD_QUANTITY = 999_999_999;

test.describe("Worker sale allocation (#612)", () => {
  test(
    "never leaks grade/quantity/flock detail to a restricted Worker on an insufficient-stock confirm",
    async ({ page, signIn, farm }) => {
      await signIn(restrictedWorker());
      const today = farmToday(farm.timeZoneId);
      const customerName = `E2E Worker Sale ${Date.now()}`;

      await page.goto("/customers");
      await page.getByRole("button", { name: tEn("customers:newCustomerButton") }).click();
      const customerDialog = page.getByRole("dialog", { name: tEn("customers:newCustomerButton") });
      await customerDialog.getByLabel(tEn("customers:nameFieldLabel")).fill(customerName);
      await customerDialog.getByLabel(tEn("customers:phoneFieldLabel")).fill("555-0100");
      await customerDialog.getByRole("button", { name: tEn("customers:addCustomerButton") }).click();
      await expect(customerDialog).toBeHidden();

      await page.goto("/sales");
      await page.getByRole("button", { name: tEn("sales:newOrder") }).click();
      const orderDialog = page.getByRole("dialog", { name: tEn("sales:newOrder") });
      await selectOptionContaining(orderDialog.getByLabel(tEn("sales:customer")), customerName);
      await orderDialog.getByLabel(tEn("sales:date")).fill(today);
      await orderDialog.getByRole("button", { name: tEn("sales:newDraftOrder") }).click();
      await expect(orderDialog).toBeHidden();

      await selectOptionContaining(page.getByLabel(tEn("sales:product")), PRODUCT);
      await page
        .getByLabel(tEn("sales:quantityWithUnit", { unit: tEn("sales:unitEgg").toLowerCase() }),
          { exact: true })
        .fill(String(ABSURD_QUANTITY));
      await page.getByLabel(tEn("sales:unitPriceWithCurrency", { code: farm.currencyCode }))
        .fill("0.01");
      await page.getByRole("button", { name: tEn("sales:addLine") }).click();

      const lineItems = page
        .getByRole("table")
        .filter({ has: page.getByRole("columnheader", { name: tEn("sales:product") }) });
      await expect(lineItems.getByRole("row").filter({ hasText: PRODUCT })).toHaveCount(1);

      await page.getByRole("button", { name: tEn("sales:confirmOrderButton") }).click();

      // Which branch this spec takes is decided by the confirm RESPONSE, so the
      // wait is registered before the click that fires it. The previous shape
      // asked `pageError.isVisible()` immediately after the click — a
      // non-waiting check that returned false while the 422 was still in
      // flight, sending the test into the success branch and timing out on
      // "Expected Confirmed, received '[Draft]'" (CI, line 115).
      const confirmSettled = page.waitForResponse(
        (r) => r.url().includes("/api/v1/sales/")
          && r.url().endsWith("/confirm")
          && r.request().method() === "POST",
      );
      await page
        .getByRole("dialog", { name: tEn("sales:confirmOrderTitle") })
        .getByRole("button", { name: tEn("sales:confirmOrderConfirmLabel") })
        .click();
      const confirmResponse = await confirmSettled;

      if (confirmResponse.ok()) {
        // The seed somehow covered even this — the order confirmed instead.
        await expect(page.getByRole("heading", { name: new RegExp(customerName.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")) }))
          .toContainText(tEn("enums:status.Confirmed"));
      } else {
        const pageError = page.locator("p.error");
        await expect(pageError).toBeVisible();
        // #612's whole point: an absurd quantity for a restricted Worker must
        // NEVER surface the detailed grade/quantity message a non-restricted
        // caller gets for the identical shortfall. Asserted only once the error
        // has actually rendered — before that the count is trivially 0.
        await expect(pageError.filter({ hasText: /unallocated/ })).toHaveCount(0);
        const text = await pageError.textContent();
        const genericAssignedOnly = tEn("errors:EggLot.AssignedFlocksInsufficientStock");
        // EggLot.InsufficientStock (the both-insufficient / AllFarmFlocks-
        // insufficient branch) has no catalog entry yet (#612 scoped the
        // translation to the distinct assigned-flocks code only), so it
        // still renders the server's plain English default text.
        const genericFarmWide = "There is not enough stock available to confirm this sale.";
        expect([genericAssignedOnly, genericFarmWide]).toContain(text?.trim());
      }
    },
  );
});
