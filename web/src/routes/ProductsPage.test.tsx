import { describe, it, expect, vi, beforeEach } from "vitest";
import { screen, within, fireEvent, act } from "@testing-library/react";
import { ProductsPage } from "./ProductsPage";
import { renderWithProviders } from "../test/renderWithProviders";
import {
  activateProduct, createProduct, deactivateProduct, getAccount,
  listEggGrades, listEggUnitConversions, listProducts,
  updateEggUnitConversion, updateProduct,
} from "../api/cluckwork";
import type { Account, EggGrade, EggUnitConversion, Product } from "../api/cluckwork";
import { ApiError } from "../api/client";

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

const GRADE_A: EggGrade = { id: "g1", farmId: "f", name: "Grade A", gradeType: "Size", sortOrder: 1, isSaleable: true, active: true };
const GRADE_B: EggGrade = { id: "g2", farmId: "f", name: "Grade B", gradeType: "Size", sortOrder: 2, isSaleable: true, active: true };

// KWD (3 decimals) throughout so a hard-coded 2-decimal money path fails: 500
// minor units renders "0.500 KWD" and typed "0.5" parses to 500 (not 50).
const ACCOUNT: Account = { id: "a1", name: "Farm", currencyCode: "KWD", currencyMinorUnit: 3 };

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
  it("shows the loading placeholder until the catalog resolves", () => {
    mockListProducts.mockReturnValue(new Promise(() => {})); // never resolves
    renderWithProviders(<ProductsPage />, { token: ADMIN });
    expect(screen.getByText("Loading…")).toBeInTheDocument();
  });

  // Mount-effect error branch (catch → "Could not load…") is intentionally not
  // tested: awaiting a rejected mount promise trips a Vitest3+React19
  // unhandled-rejection false positive.

  it("renders each product with grade, unit, price and status", async () => {
    await renderReady(ADMIN);

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

describe("ProductsPage create", () => {
  it("creates a product with the full form body at the account currency scale, then clears the name/price/notes", async () => {
    mockCreate.mockResolvedValue({ id: "p9" });
    await renderReady(ADMIN);

    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "Jumbo Carton" } });
    fireEvent.change(screen.getByLabelText("Grade"), { target: { value: "g2" } });       // off the "" default
    fireEvent.change(screen.getByLabelText("Sold per"), { target: { value: "Flat" } });  // off the "Dozen" default
    fireEvent.change(screen.getByLabelText(/Default price/), { target: { value: "0.5" } }); // KWD 3dp → 500
    fireEvent.change(screen.getByLabelText("Notes"), { target: { value: "bulk" } });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Add product" }));
    });

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
    expect(screen.getByLabelText("Name")).toHaveValue(""); // reset on success
    expect(screen.getByLabelText(/Default price/)).toHaveValue(null);
    expect(screen.getByLabelText("Notes")).toHaveValue("");
  });

  it("sends notes: null when the notes field is left blank", async () => {
    mockCreate.mockResolvedValue({ id: "p9" });
    await renderReady(ADMIN);

    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "No-Notes Product" } });
    fireEvent.change(screen.getByLabelText("Grade"), { target: { value: "g1" } });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Add product" }));
    });

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
    const fill = () => {
      fireEvent.change(screen.getByLabelText("Name"), { target: { value: "Repeatable" } });
      fireEvent.change(screen.getByLabelText("Grade"), { target: { value: "g1" } });
    };

    fill();
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Add product" })); });
    expect(await screen.findByText(/Server error|boom/)).toBeInTheDocument();

    fill();
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Add product" })); });

    fill();
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Add product" })); });

    const k1 = mockCreate.mock.calls[0][1];
    const k2 = mockCreate.mock.calls[1][1];
    const k3 = mockCreate.mock.calls[2][1];
    expect(k2).toBe(k1);      // failure kept the key → exact replay
    expect(k3).not.toBe(k2);  // success rotated it → the next write is fresh
  });
});

describe("ProductsPage inline edit", () => {
  it("saves an edit with the changed name/grade/unit/price at the product's own currency scale", async () => {
    mockUpdate.mockResolvedValue(undefined);
    await renderReady(ADMIN);

    // The <tr> node is stable across the switch to edit mode — reuse it rather
    // than re-finding by a name that depends on the edited input values.
    const rowP1 = screen.getByRole("row", { name: /Grade A Dozen/ });
    fireEvent.click(within(rowP1).getByRole("button", { name: "edit" }));

    const combos = within(rowP1).getAllByRole("combobox"); // DOM order: [0] grade, [1] unit
    fireEvent.change(within(rowP1).getByRole("textbox"), { target: { value: "Grade A Half-Dozen" } });
    fireEvent.change(combos[0], { target: { value: "g2" } });      // grade g1 → g2
    fireEvent.change(combos[1], { target: { value: "Carton" } });  // unit Dozen → Carton
    fireEvent.change(within(rowP1).getByRole("spinbutton"), { target: { value: "1.25" } }); // KWD 3dp → 1250
    await act(async () => {
      fireEvent.click(within(rowP1).getByRole("button", { name: "save" }));
    });

    expect(mockUpdate.mock.calls[0][0]).toBe("p1");
    // notes carries the seeded "premium" (there's no inline notes field to edit).
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
    fireEvent.change(within(rowCarton).getByRole("spinbutton"), { target: { value: "18" } }); // 30 → 18
    fireEvent.click(within(rowCarton).getByRole("checkbox")); // active true → false
    await act(async () => {
      fireEvent.click(within(rowCarton).getByRole("button", { name: "save" }));
    });

    expect(mockUpdateConversion.mock.calls[0][0]).toBe("conv-carton");
    expect(mockUpdateConversion.mock.calls[0][1]).toEqual({ eggsPerUnit: 18, active: false });
    expect(mockUpdateConversion.mock.calls[0][2]).toEqual(expect.any(String)); // idempotency key
  });
});

describe("ProductsPage role gating", () => {
  it("renders read-only for a non-admin — no create form, no row actions, no Actions columns", async () => {
    await renderReady(WORKER);

    expect(screen.queryByRole("button", { name: "Add product" })).not.toBeInTheDocument();
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
