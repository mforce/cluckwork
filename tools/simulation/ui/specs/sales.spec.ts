// Sales persona — #277's flow, end to end in one test:
// customer -> draft order -> add lines -> confirm -> record payment.
//
// ================== WHY THIS IS ONE TEST AND NOT FIVE ==================
//
// Each step consumes the previous step's output — there is no "confirm an order"
// without an order, and no "record a payment" without a confirmed one. Split
// across separate tests, either each one re-drives the whole chain (four times
// the wall clock to assert the same four things) or they share state through the
// file and become order-dependent, which is the same thing as one test but with
// a misleading report. So: one test, with the intermediate assertions in place
// as it goes, and a failure names the step it reached.
//
// ================== WHAT EACH RUN CONSUMES ==================
//
// Confirming the order ALLOCATES stock FIFO, so every run permanently draws 10
// units of the seeded product out of the fixture and nothing puts them back.
// That is fine for a throwaway fixture and for the dozens of runs a working
// session involves — the seed carries 528 egg lots — but it is not free, and
// unlike manager.spec.ts's flock growth it is a DECREASING resource. If this
// ever starts failing at "add line" or "confirm" after many un-reset runs, the
// cause is an exhausted grade, not a regression: run `reset.sh`.
//
// It creates its own customer rather than reusing a seeded one. The alternative
// — picking `Sim Customer 1` — makes the run depend on the fixture's customer
// having no confirmed order already carrying an outstanding balance, which is
// exactly the sort of hidden coupling that turns into a flake three months from
// now. A fresh customer starts at a known zero.

import { expect, test } from "../src/fixtures";
import { castMember } from "../src/cast";
import { commitNamedPicker, selectOptionContaining } from "../src/dom";
import { farmToday } from "../src/farm";
import { tEn } from "../src/i18n";

/** The seeded product this order sells. `defaultPriceMinorUnits` is 45 (=$0.45). */
const PRODUCT = "Sim Large Eggs";

const QUANTITY = 10;
const UNIT_PRICE = "0.50";
/** 10 x $0.50. Asserted as a number, not a formatted string — see below. */
const EXPECTED_TOTAL_MINOR = 500;

test.describe("Sales", () => {
  test("takes an order from new customer through to a recorded payment", async ({
    page,
    signIn,
    farm,
  }) => {
    await signIn(castMember("Sales"));
    const today = farmToday(farm.timeZoneId);

    // A name unique to this run. `Date.now()` is fine in a spec (the ban on it
    // is for workflow scripts, which must stay replayable); here a collision
    // between two runs against the same un-reset fixture is the real hazard.
    const customerName = `E2E Customer ${Date.now()}`;

    // ---- 1. Create the customer --------------------------------------------
    await page.goto("/customers");
    await page.getByRole("button", { name: tEn("customers:newCustomerButton") }).click();

    // Scope to the dialog. `customers:newCustomerButton` is BOTH the page's
    // trigger and the dialog's title, so an unscoped locator is ambiguous the
    // moment the dialog opens.
    const customerDialog = page.getByRole("dialog", { name: tEn("customers:newCustomerButton") });
    await customerDialog.getByLabel(tEn("customers:nameFieldLabel")).fill(customerName);
    await customerDialog.getByLabel(tEn("customers:phoneFieldLabel")).fill("555-0100");
    await customerDialog.getByRole("button", { name: tEn("customers:addCustomerButton") }).click();

    // The guarantee: the customer is in the farm's list, not that a dialog closed.
    await expect(customerDialog).toBeHidden();
    await expect(page.getByRole("cell", { name: customerName })).toBeVisible();

    // ---- 2. Open a draft order for them ------------------------------------
    await page.goto("/sales");
    await page.getByRole("button", { name: tEn("sales:newOrder") }).click();

    const orderDialog = page.getByRole("dialog", { name: tEn("sales:newOrder") });
    await commitNamedPicker(orderDialog, tEn("sales:customer"), customerName);
    await orderDialog.getByLabel(tEn("sales:date")).fill(today);
    await orderDialog.getByRole("button", { name: tEn("sales:newDraftOrder") }).click();
    await expect(orderDialog).toBeHidden();

    // The order panel's heading carries "{reference} — {customer} [{status}]",
    // so finding the customer's name in a heading IS the order having opened.
    const panelHeading = page.getByRole("heading", { name: new RegExp(escapeRegExp(customerName)) });
    await expect(panelHeading).toBeVisible();

    // ---- 3. Add a line -----------------------------------------------------
    await selectOptionContaining(page.getByLabel(tEn("sales:product")), PRODUCT);
    // #445 — the quantity label names the selected unit. The sim products all
    // sell by the individual egg (SimulationDataSeeder: "sold in individual
    // eggs"), so picking PRODUCT above lands the unit on Egg.
    await page
      .getByLabel(tEn("sales:quantityWithUnit", { unit: tEn("sales:unitEgg").toLowerCase() }),
        { exact: true })
      .fill(String(QUANTITY));
    await page.getByLabel(tEn("sales:unitPriceWithCurrency", { code: farm.currencyCode }))
      .fill(UNIT_PRICE);
    await page.getByRole("button", { name: tEn("sales:addLine") }).click();

    // The line is on the order — a row bearing the product, in the line-items
    // table. Asserting "the add button was clicked" would pass against a line
    // the server rejected.
    const lineItems = page
      .getByRole("table")
      .filter({ has: page.getByRole("columnheader", { name: tEn("sales:product") }) });
    await expect(lineItems.getByRole("row").filter({ hasText: PRODUCT })).toHaveCount(1);

    // ---- 4. Confirm --------------------------------------------------------
    await page.getByRole("button", { name: tEn("sales:confirmOrderButton") }).click();
    // Confirming routes through the shared useConfirm() dialog — the order is
    // NOT confirmed by the first click, and a spec that stopped there would be
    // asserting on a draft.
    await page
      .getByRole("dialog", { name: tEn("sales:confirmOrderTitle") })
      .getByRole("button", { name: tEn("sales:confirmOrderConfirmLabel") })
      .click();

    // Status moved. `statusLabel("Confirmed")` is the app's own enum labelling,
    // so this reads the same string the screen renders.
    await expect(panelHeading).toContainText(tEn("enums:status.Confirmed"));
    // And the draft-only affordances are gone, which is what "confirmed" means
    // to the user: no more editing.
    await expect(page.getByRole("button", { name: tEn("sales:addLine") })).toBeHidden();

    // ---- 5. Record a payment ----------------------------------------------
    // `sales:recordPayment` labels BOTH the trigger and the dialog's submit, so
    // the trigger is taken before the dialog exists and the submit is taken
    // scoped to the dialog. Getting this wrong is a strict-mode violation, not
    // a silent pass — but the scoping documents the intent.
    await page.getByRole("button", { name: tEn("sales:recordPayment") }).click();
    const payDialog = page.getByRole("dialog", { name: tEn("sales:recordPayment") });
    await payDialog.getByLabel(tEn("sales:date")).fill(today);
    await payDialog
      .getByLabel(tEn("sales:amountWithCurrency", { code: farm.currencyCode }))
      .fill(minorToMajor(EXPECTED_TOTAL_MINOR, farm));
    await payDialog.getByLabel(tEn("sales:method")).selectOption("Cash");
    await payDialog.getByRole("button", { name: tEn("sales:recordPayment") }).click();
    await expect(payDialog).toBeHidden();

    // THE GUARANTEE: the payment is on the order and the balance is settled.
    const payments = page
      .getByRole("table")
      .filter({ has: page.getByRole("columnheader", { name: tEn("sales:method") }) });
    await expect(payments.getByRole("row").filter({ hasText: tEn("sales:methodCash") }))
      .toHaveCount(1);

    // Fully paid means the "record payment" affordance is withdrawn — SalesPage
    // renders it only while `outstandingMinorUnits > 0`. That is a stronger and
    // far more stable assertion than parsing a formatted currency string out of
    // the summary paragraph, which would depend on the farm's locale.
    await expect(
      page.getByRole("button", { name: tEn("sales:recordPayment") }),
      "the order still offers 'record payment', so the payment did not settle the balance",
    ).toBeHidden();
  });
});

/**
 * Minor units -> the major-unit string a currency input expects (500 -> "5.00").
 *
 * Scaled by the farm's OWN `currencyMinorUnit` rather than a hardcoded 100. The
 * seeded farm is USD/2 today, but a zero-decimal currency (JPY, minorUnit 0)
 * would make a `/100` silently pay a hundredth of the balance — and the spec
 * would then fail at the very last assertion, pointing at the payment feature
 * rather than at its own arithmetic.
 */
function minorToMajor(minor: number, farm: { currencyMinorUnit: number }): string {
  return (minor / 10 ** farm.currencyMinorUnit).toFixed(farm.currencyMinorUnit);
}

function escapeRegExp(s: string): string {
  return s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}
