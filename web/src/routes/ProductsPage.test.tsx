import { describe, it, expect, vi, beforeEach } from "vitest";
import { screen, within, fireEvent, act } from "@testing-library/react";
import { ProductsPage } from "./ProductsPage";
import { renderWithProviders } from "../test/renderWithProviders";
import { account, NO_RECORD_HISTORY } from "../test/fixtures";
import {
  activateProduct, createProduct, deactivateProduct, getAccount,
  listEggGrades, listEggUnitConversions, listProducts,
  updateEggUnitConversion, updateProduct,
} from "../api/cluckwork";
import type { Account, EggGrade, EggUnitConversion, Product } from "../api/cluckwork";
import { ApiError } from "../api/client";
import i18n from "../i18n";

// Keep the REAL formatMoney (renders the price column at the product's own
// currency scale); stub only the network seam. Every network call the screen
// can make is stubbed — even ones a given test doesn't trigger — so a stray
// click can't hit the real fetch client. Screen uses useAuth + router → render
// via renderWithProviders.
vi.mock("../api/cluckwork", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api/cluckwork")>();
  return {
    ...actual,
    listProducts: vi.fn(),
    listEggUnitConversions: vi.fn(),
    listEggGrades: vi.fn(),
    getAccount: vi.fn(),
    createProduct: vi.fn(),
    updateProduct: vi.fn(),
    deactivateProduct: vi.fn(),
    activateProduct: vi.fn(),
    updateEggUnitConversion: vi.fn(),
    getFlock: vi.fn(),
  getCustomer: vi.fn(),
};
});

const mockListProducts = vi.mocked(listProducts);
const mockListConversions = vi.mocked(listEggUnitConversions);
const mockListGrades = vi.mocked(listEggGrades);
const mockGetAccount = vi.mocked(getAccount);
const mockCreate = vi.mocked(createProduct);
const mockUpdate = vi.mocked(updateProduct);
const mockDeactivate = vi.mocked(deactivateProduct);
const mockActivate = vi.mocked(activateProduct);
const mockUpdateConversion = vi.mocked(updateEggUnitConversion);

const GRADE_A: EggGrade = { ...NO_RECORD_HISTORY, id: "g1", farmId: "f", name: "Grade A", gradeType: "Size", sortOrder: 1, isSaleable: true, dailyEntryKind: "Manual", active: true };
const GRADE_B: EggGrade = { ...NO_RECORD_HISTORY, id: "g2", farmId: "f", name: "Grade B", gradeType: "Size", sortOrder: 2, isSaleable: true, dailyEntryKind: "Manual", active: true };

// KWD (3 decimals) throughout so a hard-coded 2-decimal money path fails: 500
// minor units renders "0.500 KWD" and typed "0.5" parses to 500 (not 50).
const ACCOUNT: Account = account({ name: "Farm", currencyCode: "KWD", currencyMinorUnit: 3 });

const P1: Product = {
  id: "p1", name: "Grade A Dozen", productType: "Egg", defaultUnit: "Dozen",
  defaultPriceMinorUnits: 500, currencyCode: "KWD", currencyMinorUnit: 3,
  eggGradeId: "g1", notes: "premium", active: true, version: 1,
};
const P2: Product = {
  id: "p2", name: "Legacy Tray", productType: "Egg", defaultUnit: "Tray",
  defaultPriceMinorUnits: null, currencyCode: "KWD", currencyMinorUnit: 3,
  eggGradeId: "g2", notes: null, active: false, version: 1,
};

// "Individual" is the fixed 1-egg row (no edit); "Carton" is an editable
// conversion whose unitCode collides with no product row above.
const CONV_INDIVIDUAL: EggUnitConversion = { id: "conv-ind", unitCode: "Individual", eggsPerUnit: 1, active: true, version: 1 };
const CONV_CARTON: EggUnitConversion = { id: "conv-carton", unitCode: "Carton", eggsPerUnit: 30, active: true, version: 2 };

const ADMIN = { sub: "u1", role: "Admin" };
const WORKER = { sub: "u1" }; // no role → Worker → not admin

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  mockListProducts.mockResolvedValue([P1, P2]);
  mockListConversions.mockResolvedValue([CONV_INDIVIDUAL, CONV_CARTON]);
  mockListGrades.mockResolvedValue([GRADE_A, GRADE_B]);
  mockGetAccount.mockResolvedValue(ACCOUNT);
});

async function renderReady(token: Record<string, unknown>) {
  renderWithProviders(<ProductsPage />, { token });
  await screen.findByRole("row", { name: /Grade A Dozen/ });
}

describe("ProductsPage loading + display", () => {
  it("shows the loading placeholder until the catalog resolves, then swaps in the rows", async () => {
    let resolveProducts!: (value: Product[]) => void;
    mockListProducts.mockReturnValue(new Promise<Product[]>((resolve) => { resolveProducts = resolve; }));
    renderWithProviders(<ProductsPage />, { token: ADMIN });
    expect(screen.getByText("Loading…")).toBeInTheDocument();

    // Resolve the deferred catalog: the loading text must give way to a real row.
    await act(async () => {
      resolveProducts([P1, P2]);
    });
    expect(await screen.findByRole("row", { name: /Grade A Dozen/ })).toBeInTheDocument();
    expect(screen.queryByText("Loading…")).not.toBeInTheDocument();
  });

  // Mount-effect error branch (catch → "Could not load…") is intentionally not
  // tested: awaiting a rejected mount promise trips a Vitest3+React19
  // unhandled-rejection false positive.

  it("renders each product with grade, unit, price and status", async () => {
    await renderReady(ADMIN);

    // Mount load must request inactive items, else the Inactive Legacy row
    // could only render by accident of the mock, not the real query.
    expect(mockListProducts).toHaveBeenCalledWith({ includeInactive: true });

    const rowP1 = screen.getByRole("row", { name: /Grade A Dozen/ });
    expect(within(rowP1).getByText("Grade A")).toBeInTheDocument(); // grade name resolved by id
    expect(within(rowP1).getByText("Dozen")).toBeInTheDocument();   // sold-per unit
    expect(within(rowP1).getByText("0.500 KWD")).toBeInTheDocument(); // 500 @ scale 3 — not "5.00"
    expect(within(rowP1).getByText("Active")).toBeInTheDocument();

    const rowP2 = screen.getByRole("row", { name: /Legacy Tray/ });
    expect(within(rowP2).getByText("Grade B")).toBeInTheDocument();
    expect(within(rowP2).getByText("—")).toBeInTheDocument(); // null price
    expect(within(rowP2).getByText("Inactive")).toBeInTheDocument();
  });

  it("shows the empty-state hint when there are no products", async () => {
    mockListProducts.mockResolvedValue([]);
    renderWithProviders(<ProductsPage />, { token: ADMIN });
    expect(await screen.findByText("No products yet.")).toBeInTheDocument();
  });
});

// F131: create/edit moved into dialogs — open first, same assertions after.
const openCreate = () => fireEvent.click(screen.getByRole("button", { name: "New product" }));
const dialog = () => screen.getByRole("dialog");
const submitCreate = async () => {
  await act(async () => {
    fireEvent.click(within(dialog()).getByRole("button", { name: "Add product" }));
  });
};

describe("ProductsPage create", () => {
  it("creates a product with the full form body at the account currency scale, then clears the name/price/notes", async () => {
    mockCreate.mockResolvedValue({ id: "p9" });
    await renderReady(ADMIN);
    openCreate();

    fireEvent.change(within(dialog()).getByLabelText("Name"), { target: { value: "Jumbo Carton" } });
    fireEvent.change(within(dialog()).getByLabelText("Grade"), { target: { value: "g2" } });       // off the "" default
    fireEvent.change(within(dialog()).getByLabelText("Sold per"), { target: { value: "Flat" } });  // off the "Dozen" default
    fireEvent.change(within(dialog()).getByLabelText(/Default price/), { target: { value: "0.5" } }); // KWD 3dp → 500
    fireEvent.change(within(dialog()).getByLabelText("Notes"), { target: { value: "bulk" } });
    await submitCreate();

    // Full body with non-default values → a hard-coded body (or a 2dp price
    // path, which would send 50) fails here.
    expect(mockCreate.mock.calls[0][0]).toEqual({
      name: "Jumbo Carton",
      productType: "Egg",
      defaultUnit: "Flat",
      defaultPriceMinorUnits: 500,
      eggGradeId: "g2",
      notes: "bulk",
    });
    expect(mockCreate.mock.calls[0][1]).toEqual(expect.any(String)); // idempotency key
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument(); // success dismisses it
    openCreate();
    expect(within(dialog()).getByLabelText("Name")).toHaveValue(""); // reset on success
    expect(within(dialog()).getByLabelText(/Default price/)).toHaveValue(null);
    expect(within(dialog()).getByLabelText("Notes")).toHaveValue("");
  });

  it("sends notes: null when the notes field is left blank", async () => {
    mockCreate.mockResolvedValue({ id: "p9" });
    await renderReady(ADMIN);
    openCreate();

    fireEvent.change(within(dialog()).getByLabelText("Name"), { target: { value: "No-Notes Product" } });
    fireEvent.change(within(dialog()).getByLabelText("Grade"), { target: { value: "g1" } });
    await submitCreate();

    const body = mockCreate.mock.calls[0][0];
    expect(body.notes).toBeNull();
    expect(body.defaultPriceMinorUnits).toBeNull(); // blank price → null, server uses no default
  });
});

describe("ProductsPage idempotency-key contract (create)", () => {
  it("replays the SAME key after a failed create, then rotates it after success", async () => {
    mockCreate.mockRejectedValueOnce(new ApiError(500, "Server error", "boom"));
    mockCreate.mockResolvedValue({ id: "p9" });
    await renderReady(ADMIN);
    openCreate();
    const fill = () => {
      fireEvent.change(within(dialog()).getByLabelText("Name"), { target: { value: "Repeatable" } });
      fireEvent.change(within(dialog()).getByLabelText("Grade"), { target: { value: "g1" } });
    };

    fill();
    await submitCreate();
    // a failed create keeps the dialog up, with the error inside it
    expect(within(dialog()).getByText(/Server error|boom/)).toBeInTheDocument();

    fill();
    await submitCreate();

    openCreate(); // success closed it
    fill();
    await submitCreate();

    const k1 = mockCreate.mock.calls[0][1];
    const k2 = mockCreate.mock.calls[1][1];
    const k3 = mockCreate.mock.calls[2][1];
    expect(k2).toBe(k1);      // failure kept the key → exact replay
    expect(k3).not.toBe(k2);  // success rotated it → the next write is fresh
  });
});

describe("ProductsPage edit", () => {
  it("saves an edit with the changed name/grade/unit/price at the product's own currency scale", async () => {
    mockUpdate.mockResolvedValue(undefined);
    await renderReady(ADMIN);

    const rowP1 = screen.getByRole("row", { name: /Grade A Dozen/ });
    fireEvent.click(within(rowP1).getByRole("button", { name: "edit" }));

    // The dialog is seeded from the row before anything is changed.
    expect(within(dialog()).getByLabelText("Name")).toHaveValue("Grade A Dozen");
    fireEvent.change(within(dialog()).getByLabelText("Name"), { target: { value: "Grade A Half-Dozen" } });
    fireEvent.change(within(dialog()).getByLabelText("Grade"), { target: { value: "g2" } });     // grade g1 → g2
    fireEvent.change(within(dialog()).getByLabelText("Sold per"), { target: { value: "Carton" } }); // unit Dozen → Carton
    fireEvent.change(within(dialog()).getByLabelText(/Default price/), { target: { value: "1.25" } }); // KWD 3dp → 1250
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Save" }));
    });

    expect(mockUpdate.mock.calls[0][0]).toBe("p1");
    // notes carries the seeded "premium" — the dialog seeds it and it is untouched.
    expect(mockUpdate.mock.calls[0][1]).toEqual({
      name: "Grade A Half-Dozen",
      defaultUnit: "Carton",
      defaultPriceMinorUnits: 1250,
      eggGradeId: "g2",
      notes: "premium",
    });
    expect(mockUpdate.mock.calls[0][2]).toEqual(expect.any(String)); // idempotency key
  });

  it("deactivates an active product and activates an inactive one", async () => {
    mockDeactivate.mockResolvedValue(undefined);
    mockActivate.mockResolvedValue(undefined);
    await renderReady(ADMIN);

    await act(async () => {
      fireEvent.click(within(screen.getByRole("row", { name: /Grade A Dozen/ })).getByRole("button", { name: "deactivate" }));
    });
    expect(mockDeactivate).toHaveBeenCalledWith("p1", expect.any(String));

    await act(async () => {
      fireEvent.click(within(screen.getByRole("row", { name: /Legacy Tray/ })).getByRole("button", { name: "activate" }));
    });
    expect(mockActivate).toHaveBeenCalledWith("p2", expect.any(String));
  });
});

describe("ProductsPage packed-unit conversions", () => {
  it("shows a fixed '1' with no edit for the Individual unit", async () => {
    await renderReady(ADMIN);
    const rowInd = screen.getByRole("row", { name: /Individual/ });
    expect(within(rowInd).getByText("always 1")).toBeInTheDocument();
    expect(within(rowInd).queryByRole("button", { name: "edit" })).not.toBeInTheDocument();
  });

  it("saves an edited conversion with the new eggs-per-unit and active flag", async () => {
    mockUpdateConversion.mockResolvedValue(undefined);
    await renderReady(ADMIN);

    const rowCarton = screen.getByRole("row", { name: /Carton/ });
    fireEvent.click(within(rowCarton).getByRole("button", { name: "edit" }));
    // The dialog is titled for the unit it edits, so two packed units can't be confused.
    expect(dialog()).toHaveAccessibleName("Eggs per Carton");
    fireEvent.change(within(dialog()).getByRole("spinbutton", { name: "Eggs per unit" }), { target: { value: "18" } }); // 30 → 18
    fireEvent.click(within(dialog()).getByLabelText("active")); // active true → false
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Save" }));
    });

    expect(mockUpdateConversion.mock.calls[0][0]).toBe("conv-carton");
    expect(mockUpdateConversion.mock.calls[0][1]).toEqual({ eggsPerUnit: 18, active: false });
    expect(mockUpdateConversion.mock.calls[0][2]).toEqual(expect.any(String)); // idempotency key
  });
});

describe("ProductsPage edit validation parity", () => {
  // The row's save used to be a plain button, so the browser never enforced
  // min/step and an over-precise price reached the screen's own parser. The
  // dialog turned that button into a form submit, which would hand the check
  // to native validation and swallow the specific message — hence noValidate.
  it("surfaces the currency-scale message rather than letting the browser block the submit", async () => {
    await renderReady(ADMIN);
    fireEvent.click(within(screen.getByRole("row", { name: /Grade A Dozen/ })).getByRole("button", { name: "edit" }));

    // KWD is 3dp: a 4th decimal is the parser's error, not the browser's.
    fireEvent.change(within(dialog()).getByLabelText(/Default price/), { target: { value: "1.2345" } });
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Save" }));
    });

    expect(within(dialog()).getByText("At most 3 decimal places for this currency.")).toBeInTheDocument();
    expect(mockUpdate).not.toHaveBeenCalled();
  });
});

describe("ProductsPage dialog dismissal", () => {
  it("closes the create dialog on Cancel without writing", async () => {
    await renderReady(ADMIN);
    openCreate();
    fireEvent.change(within(dialog()).getByLabelText("Name"), { target: { value: "Abandoned" } });

    fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));

    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(mockCreate).not.toHaveBeenCalled();
  });

  it("closes the edit dialog on Cancel without writing", async () => {
    await renderReady(ADMIN);
    fireEvent.click(within(screen.getByRole("row", { name: /Grade A Dozen/ })).getByRole("button", { name: "edit" }));

    fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));

    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(mockUpdate).not.toHaveBeenCalled();
  });

  it("closes the packed-unit dialog on Cancel without writing", async () => {
    await renderReady(ADMIN);
    fireEvent.click(within(screen.getByRole("row", { name: /Carton/ })).getByRole("button", { name: "edit" }));

    fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));

    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(mockUpdateConversion).not.toHaveBeenCalled();
  });
});

// #479 — one slot per PLACE a message can appear. ProductsPage has three
// dialogs (create, edit product, edit conversion); it has no background READ
// outside the mount load (which blocks the whole screen on failure, so it
// can never race an open dialog). Deactivate/activate are the screen's other
// failure source — row writes with no dialog of their own — so they stand in
// for "the page's own failure" in the isolation tests below.
describe("ProductsPage error placement (#479)", () => {
  it("shows a failed create inside the dialog, not on the page behind it", async () => {
    mockCreate.mockRejectedValue(new ApiError(422, "Validation failed", "Name already used."));
    await renderReady(ADMIN);
    openCreate();
    fireEvent.change(within(dialog()).getByLabelText("Name"), { target: { value: "Jumbo Carton" } });
    fireEvent.change(within(dialog()).getByLabelText("Grade"), { target: { value: "g1" } });
    await submitCreate();

    expect(within(dialog()).getByText("Name already used.")).toBeInTheDocument();
    // Exactly one copy: the page must not render the dialog's message too.
    expect(screen.getAllByText("Name already used.")).toHaveLength(1);
  });

  it("shows a failed edit inside the dialog, not on the page behind it", async () => {
    mockUpdate.mockRejectedValue(new ApiError(409, "Conflict", "Someone else changed this product."));
    await renderReady(ADMIN);
    fireEvent.click(within(screen.getByRole("row", { name: /Grade A Dozen/ })).getByRole("button", { name: "edit" }));
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Save" }));
    });

    expect(within(dialog()).getByText("Someone else changed this product.")).toBeInTheDocument();
    expect(screen.getAllByText("Someone else changed this product.")).toHaveLength(1);
  });

  it("shows a failed conversion save inside the dialog, not on the page behind it", async () => {
    mockUpdateConversion.mockRejectedValue(new ApiError(500, "Server error", "boom"));
    await renderReady(ADMIN);
    fireEvent.click(within(screen.getByRole("row", { name: /Carton/ })).getByRole("button", { name: "edit" }));
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Save" }));
    });

    expect(within(dialog()).getByText("boom")).toBeInTheDocument();
    expect(screen.getAllByText("boom")).toHaveLength(1);
  });

  // Displacement: a second dialog session begins without the first being
  // dismissed, so `abandon`-on-close never runs. The scope is fixed ("edit"),
  // so whatever the previous product's attempt left is sitting in the very
  // slot the next product's dialog renders (pi review of #491).
  it("does not carry one product's failed edit into another product's dialog", async () => {
    mockUpdate.mockRejectedValue(new ApiError(409, "Conflict", "Someone else changed this product."));
    await renderReady(ADMIN);
    fireEvent.click(within(screen.getByRole("row", { name: /Grade A Dozen/ })).getByRole("button", { name: "edit" }));
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Save" }));
    });
    expect(within(dialog()).getByText("Someone else changed this product.")).toBeInTheDocument();

    // The row behind the backdrop is not mouse-reachable, but #480 established
    // a screen reader's virtual cursor still gets there — the same door the
    // per-dialog map exists for.
    fireEvent.click(within(screen.getByRole("row", { name: /Legacy Tray/ })).getByRole("button", { name: "edit" }));

    // The dialog really did swap records — otherwise the assertion below would
    // pass for the wrong reason.
    expect(within(dialog()).getByLabelText("Name")).toHaveValue("Legacy Tray");
    expect(within(dialog()).queryByText("Someone else changed this product.")).not.toBeInTheDocument();
  });

  it("does not carry one conversion's failed save into another conversion's dialog", async () => {
    // A second EDITABLE conversion, added here rather than to the shared
    // fixtures: conversion→conversion is the displacement this scope has, and
    // the default catalog's only other row ("Individual") carries no edit
    // button to displace with.
    const CONV_FLAT: EggUnitConversion = { id: "conv-flat", unitCode: "Flat", eggsPerUnit: 20, active: true, version: 1 };
    mockListConversions.mockResolvedValue([CONV_INDIVIDUAL, CONV_CARTON, CONV_FLAT]);
    mockUpdateConversion.mockRejectedValue(new ApiError(500, "Server error", "boom"));
    await renderReady(ADMIN);
    fireEvent.click(within(screen.getByRole("row", { name: /Carton/ })).getByRole("button", { name: "edit" }));
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Save" }));
    });
    expect(within(dialog()).getByText("boom")).toBeInTheDocument();

    fireEvent.click(within(screen.getByRole("row", { name: /Flat/ })).getByRole("button", { name: "edit" }));
    // Swapped to the Flat conversion (20 eggs), so a leftover would be visible
    // under a heading about a different unit entirely.
    expect(within(dialog()).getByLabelText("Eggs per unit")).toHaveValue(20);
    expect(screen.queryByText("boom")).not.toBeInTheDocument();
  });

  it("keeps a deactivate failure out of an open create dialog", async () => {
    // The row stays reachable in the DOM behind a portalled dialog (jsdom
    // does not enforce the backdrop's visual occlusion), so this is a real
    // page-scope failure landing while a dialog happens to be open.
    mockDeactivate.mockRejectedValue(new ApiError(500, "Server error", "boom"));
    await renderReady(ADMIN);
    openCreate();

    await act(async () => {
      fireEvent.click(within(screen.getByRole("row", { name: /Grade A Dozen/ })).getByRole("button", { name: "deactivate" }));
    });

    expect(within(dialog()).queryByText("boom")).not.toBeInTheDocument();
    expect(screen.getByText("boom")).toBeInTheDocument();
  });

  it("keeps a deactivate failure on the page while the create dialog opens and its own write fails", async () => {
    // Two live messages at once, in their own places. The page's belongs to
    // the row action the user has not dealt with; the dialog's to the form
    // in front of them. Neither may erase the other.
    mockDeactivate.mockRejectedValue(new ApiError(500, "Server error", "boom"));
    mockCreate.mockRejectedValue(new ApiError(422, "Validation failed", "Name already used."));
    await renderReady(ADMIN);

    await act(async () => {
      fireEvent.click(within(screen.getByRole("row", { name: /Grade A Dozen/ })).getByRole("button", { name: "deactivate" }));
    });
    await screen.findByText("boom");

    openCreate();
    expect(screen.getByText("boom")).toBeInTheDocument();

    fireEvent.change(within(dialog()).getByLabelText("Name"), { target: { value: "Jumbo Carton" } });
    fireEvent.change(within(dialog()).getByLabelText("Grade"), { target: { value: "g1" } });
    await submitCreate();

    expect(within(dialog()).getByText("Name already used.")).toBeInTheDocument();
    expect(screen.getByText("boom")).toBeInTheDocument();
  });
});

describe("ProductsPage role gating", () => {
  it("renders read-only for a non-admin — no create form, no row actions, no Actions columns", async () => {
    await renderReady(WORKER);

    expect(screen.queryByRole("button", { name: "New product" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "edit" })).not.toBeInTheDocument();       // product + conversion edits
    expect(screen.queryByRole("button", { name: "deactivate" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "activate" })).not.toBeInTheDocument();    // inactive Legacy row too
    expect(screen.queryByRole("columnheader", { name: "Actions" })).not.toBeInTheDocument();
    // No admin-only mount fetch exists to assert absent: listProducts /
    // listEggUnitConversions / listEggGrades / getAccount all load for any role.
    expect(mockGetAccount).toHaveBeenCalled();
  });

  it("still renders product and price data for a non-admin (read-only view keeps the money column)", async () => {
    await renderReady(WORKER);
    const rowP1 = screen.getByRole("row", { name: /Grade A Dozen/ });
    expect(within(rowP1).getByText("0.500 KWD")).toBeInTheDocument();
  });
});

// ---------------------------------------------------------------------------
// i18n wiring (#182, Task 17, batch B3 — the last B3 screen)
// ---------------------------------------------------------------------------

// `products` is English-only (not in TRANSLATED_NAMESPACES — see
// translations-status.ts), so under ANY UI language the rendered text falls
// back to this exact English string, same as a still-hardcoded literal would
// render — asserting it, even under a non-English locale, would prove nothing
// (CONTRIBUTING-i18n.md's fallback trap). Swap the catalog value at runtime
// instead, the same i18n.addResource technique the other batches use, so each
// marker only renders if the screen actually reads the catalog rather than a
// literal that happens to still match it.
describe("ProductsPage i18n wiring (#182, Task 17)", () => {
  function withOverride(ns: string, key: string, value: string, run: () => Promise<void> | void) {
    const original = i18n.getResource("en", ns, key) as string;
    i18n.addResource("en", ns, key, value);
    return Promise.resolve(run()).finally(() => {
      i18n.addResource("en", ns, key, original);
    });
  }

  it("reads the heading from the catalog, not a hardcoded literal", async () => {
    await withOverride("products", "title", "TITLE-MARKER", async () => {
      await renderReady(ADMIN);
      expect(screen.getByRole("heading", { name: "TITLE-MARKER" })).toBeInTheDocument();
      expect(screen.queryByRole("heading", { name: "Products" })).not.toBeInTheDocument();
    });
  });

  it("reads the new-product button label from the catalog, not a hardcoded literal", async () => {
    await withOverride("products", "newProductButton", "NEW-PRODUCT-MARKER", async () => {
      await renderReady(ADMIN);
      expect(screen.getByRole("button", { name: "NEW-PRODUCT-MARKER" })).toBeInTheDocument();
      expect(screen.queryByRole("button", { name: "New product" })).not.toBeInTheDocument();
    });
  });

  // Proves the create dialog's price label template reads from the catalog
  // AND still interpolates the account's currency code (free-form DATA).
  it("interpolates the account currency code into the price label from the catalog", async () => {
    await withOverride("products", "defaultPriceWithCurrencyLabel", "PRICE-MARKER ({{code}})", async () => {
      await renderReady(ADMIN);
      openCreate();
      expect(within(dialog()).getByLabelText("PRICE-MARKER (KWD)")).toBeInTheDocument();
      expect(within(dialog()).queryByLabelText(/Default price \(KWD\)/)).not.toBeInTheDocument();
    });
  });

  // Proves the packed-unit dialog's title reads the COPY template from the
  // catalog while still interpolating the conversion's free-form unitCode
  // (DATA) — a hardcoded literal, or one that dropped the interpolation,
  // would fail this even though "Carton" itself is unaffected by the marker.
  it("interpolates the conversion's unitCode into the packed-unit dialog title from the catalog", async () => {
    await withOverride("products", "eggsPerUnit", "EGGS-MARKER {{unitCode}} END", async () => {
      await renderReady(ADMIN);
      fireEvent.click(within(screen.getByRole("row", { name: /Carton/ })).getByRole("button", { name: "edit" }));
      expect(dialog()).toHaveAccessibleName("EGGS-MARKER Carton END");
    });
  });

  // Proves BOTH the products table's StatusBadge and the packed-units
  // table's plain-text cell read the `status` ENUM label from the catalog
  // (via statusLabel) — not two coincidentally-matching hardcoded literals.
  it("reads the active-status enum label from the catalog on both tables", async () => {
    await withOverride("enums", "status.Active", "ACTIVE-MARKER", async () => {
      await renderReady(ADMIN);
      const productRow = screen.getByRole("row", { name: /Grade A Dozen/ });
      expect(within(productRow).getByText("ACTIVE-MARKER")).toBeInTheDocument();
      expect(within(productRow).queryByText("Active")).not.toBeInTheDocument();

      const convRow = screen.getByRole("row", { name: /Carton/ });
      expect(within(convRow).getByText("ACTIVE-MARKER")).toBeInTheDocument();
      expect(within(convRow).queryByText("Active")).not.toBeInTheDocument();
    });
  });

  it("reads the inactive-status enum label from the catalog on the products table", async () => {
    await withOverride("enums", "status.Inactive", "INACTIVE-MARKER", async () => {
      await renderReady(ADMIN);
      const rowP2 = screen.getByRole("row", { name: /Legacy Tray/ });
      expect(within(rowP2).getByText("INACTIVE-MARKER")).toBeInTheDocument();
      expect(within(rowP2).queryByText("Inactive")).not.toBeInTheDocument();
    });
  });

  // The price parser's decimal-precision message is thrown synchronously
  // inside the submit handler (never a rejected mount promise), so it's safe
  // to exercise directly — see the imperative i18n.t() pattern.
  it("reads the price-precision validation message from the catalog, not a hardcoded literal", async () => {
    await withOverride("products", "atMostDecimals", "AT-MOST-MARKER {{count}} END", async () => {
      await renderReady(ADMIN);
      fireEvent.click(within(screen.getByRole("row", { name: /Grade A Dozen/ })).getByRole("button", { name: "edit" }));
      fireEvent.change(within(dialog()).getByLabelText(/Default price/), { target: { value: "1.2345" } });
      await act(async () => {
        fireEvent.click(within(dialog()).getByRole("button", { name: "Save" }));
      });
      expect(within(dialog()).getByText("AT-MOST-MARKER 3 END")).toBeInTheDocument();
      expect(within(dialog()).queryByText("At most 3 decimal places for this currency.")).not.toBeInTheDocument();
    });
  });

  it("reads the fixed-Individual-unit message from the catalog, not a hardcoded literal", async () => {
    await withOverride("products", "alwaysOneMessage", "ALWAYS-ONE-MARKER", async () => {
      await renderReady(ADMIN);
      const rowInd = screen.getByRole("row", { name: /Individual/ });
      expect(within(rowInd).getByText("ALWAYS-ONE-MARKER")).toBeInTheDocument();
      expect(within(rowInd).queryByText("always 1")).not.toBeInTheDocument();
    });
  });
});

// #236 — the run() helper now rides usePendingAction: while one flight is
// held open (deferred promise, client.test.ts idiom) exactly the clicked
// trigger spins and every other verb merely disables.
describe("ProductsPage pending states (#236)", () => {
  function deferred<T>() {
    let resolve!: (v: T) => void;
    let reject!: (e: unknown) => void;
    const promise = new Promise<T>((res, rej) => {
      resolve = res;
      reject = rej;
    });
    return { promise, resolve, reject };
  }

  it("spins only the clicked row verb while its flight is open, and clears on settle", async () => {
    const gate = deferred<void>();
    mockDeactivate.mockReturnValue(gate.promise);
    await renderReady(ADMIN);

    await act(async () => {
      fireEvent.click(within(screen.getByRole("row", { name: /Grade A Dozen/ }))
        .getByRole("button", { name: "deactivate" }));
    });

    const spinning = within(screen.getByRole("row", { name: /Grade A Dozen/ }))
      .getByRole("button", { name: "deactivate" });
    expect(spinning).toHaveAttribute("aria-busy", "true");
    expect(spinning).toBeDisabled();
    // The other row's verb, and the same row's edit, disable WITHOUT spinning.
    const sibling = within(screen.getByRole("row", { name: /Legacy Tray/ }))
      .getByRole("button", { name: "activate" });
    expect(sibling).toBeDisabled();
    expect(sibling).not.toHaveAttribute("aria-busy");

    await act(async () => { gate.resolve(); });
    expect(document.querySelector('[aria-busy="true"]')).toBeNull();
    expect(within(screen.getByRole("row", { name: /Legacy Tray/ }))
      .getByRole("button", { name: "activate" })).toBeEnabled();
  });

  it("closes the create dialog only after the held create settles, leaving nothing busy", async () => {
    const gate = deferred<{ id: string }>();
    mockCreate.mockReturnValue(gate.promise);
    await renderReady(ADMIN);
    openCreate();
    fireEvent.change(within(dialog()).getByLabelText("Name"), { target: { value: "Held" } });
    fireEvent.change(within(dialog()).getByLabelText("Grade"), { target: { value: "g1" } });
    await submitCreate();

    // Held: the dialog stays up with its submit as the pending indicator.
    const submit = within(dialog()).getByRole("button", { name: "Add product" });
    expect(submit).toHaveAttribute("aria-busy", "true");
    expect(submit).toBeDisabled();

    await act(async () => { gate.resolve({ id: "p9" }); });
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(document.querySelector('[aria-busy="true"]')).toBeNull();
  });
});
