# Sales

Customers and orders: take an order, confirm it (which allocates stock FIFO from egg lots),
record payment. The Dashboard's Recent sales list reads the same orders.

## Sub-features

- Customers list and "New customer" (`/customers`).
- New order: customer picker, date, lines (product, unit egg, quantity), "Add line".
- Draft order, then confirm: allocation decrements stock; an under-stocked confirm is
  refused with the shortfall.
- Record payment: method, amount; "Unpaid only" filter.
- Status vocabulary: Draft, Confirmed, Allocated, Paid, as dot plus word.

## How to get to it (user POV)

Nav: Sales & stock, Sales; the Sales tab on the phone. Route `/sales`; customers at
`/customers`. The Dashboard's "Review to confirm" link lands here filtered by customer.

## Driving it with Playwright

`sales.spec.ts` drives the whole path "takes an order from new customer through to a
recorded payment". The handles:

```ts
await page.goto("/customers");
await page.getByRole("button", { name: tEn("customers:newCustomerButton") }).click();
// fill the dialog, save
await page.goto("/sales");
await page.getByRole("button", { name: tEn("sales:newOrder") }).click();
await commitNamedPicker(page, tEn("sales:customer"), customerName);
await page.getByRole("button", { name: tEn("sales:addLine") }).click();
await commitNamedPicker(page, tEn("sales:product"), "Large");
// quantity, unit tEn("sales:unitEgg"), save the draft, confirm, then
await page.getByRole("button", { name: tEn("sales:recordPayment") }).click();
```

Proof: the order's status walks Draft, Confirmed, Paid on the list; `/stock` shows the
allocated lots decremented; `worker-sale-allocation.spec.ts` proves a Worker cannot
allocate.

## Gotchas

- Confirming needs stock: on a fresh reset the fixture has lots; after many smoke runs the
  deep grades may be short, and the refusal is a legitimate outcome to assert, not a flake.
- The searchable pickers are paged: `named-entity-picker.spec.ts` shows how a customer on
  page two is reached through search.
- Money is displayed in the farm's currency and locale; assert on the catalog string, not
  on a formatted number typed by hand.
