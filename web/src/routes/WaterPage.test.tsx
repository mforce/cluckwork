import { describe, it, expect, vi, beforeEach } from "vitest";
import { screen, within, fireEvent, waitFor, act } from "@testing-library/react";
import { WaterPage } from "./WaterPage";
import { renderWithProviders } from "../test/renderWithProviders";
import {
  listFlocks, listWaterUsage, recordWaterUsage, updateWaterUsage,
} from "../api/cluckwork";
import type { Flock, WaterUsage } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { todayIso } from "../lib/dates";

// WaterPage's only runtime deps on the API module are the four network fns it
// imports; mock exactly those. ApiError stays real (../api/client, unmocked) so
// errText's `instanceof ApiError` branch holds. useAuth rides on the real
// AuthProvider via renderWithProviders. todayIso (../lib/dates) is a real pure
// helper — left unmocked so the capture form's default date matches ours.
vi.mock("../api/cluckwork", () => ({
  listFlocks: vi.fn(),
  listWaterUsage: vi.fn(),
  recordWaterUsage: vi.fn(),
  updateWaterUsage: vi.fn(),
}));

const mockListFlocks = vi.mocked(listFlocks);
const mockListWaterUsage = vi.mocked(listWaterUsage);
const mockRecordWaterUsage = vi.mocked(recordWaterUsage);
const mockUpdateWaterUsage = vi.mocked(updateWaterUsage);

const FLOCK_A: Flock = {
  id: "f1", farmId: "farm1", houseId: "h1", name: "Hen House 1", breed: "ISA",
  placementDate: "2026-01-01", initialCount: 100, currentBirds: 98, status: "Active",
};
const FLOCK_B: Flock = { ...FLOCK_A, id: "f2", name: "Coop 2" };

// Direct-quantity record (meterStart null → the "—" meters cell, edit fills the
// quantity field). version 3 is the base a correction must send back.
const ROW: WaterUsage = {
  id: "w1", flockId: "f1", date: "2026-07-10", quantity: 12, unit: "L",
  source: "Well", meterStart: null, meterEnd: null, note: "morning", version: 3,
};
// Meter-backed record: quantity is the delta, and the meters cell renders it.
const METER_ROW: WaterUsage = {
  id: "w2", flockId: "f1", date: "2026-07-11", quantity: 74.75, unit: "L",
  source: "Municipal", meterStart: 100.5, meterEnd: 175.25, note: null, version: 1,
};

const ADMIN = { sub: "u1", role: "Admin" };
const WORKER = { sub: "u1" };

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  mockListFlocks.mockResolvedValue([FLOCK_A, FLOCK_B]);
  mockListWaterUsage.mockResolvedValue([]); // no records unless a test seeds them
});

// Mounts and waits for BOTH mount loads: the "Record water" button only exists
// once the water list resolves (rows !== null), and it only enables once the
// flock list resolves (a flockId is picked). Waiting on enabled proves both.
async function renderReadyForm(token: Record<string, unknown>) {
  renderWithProviders(<WaterPage />, { token });
  const btn = await screen.findByRole("button", { name: "Record water" });
  await waitFor(() => expect(btn).toBeEnabled());
}

describe("WaterPage loading + list", () => {
  it("shows a loading placeholder before the water list resolves", async () => {
    mockListWaterUsage.mockResolvedValue([ROW]);
    renderWithProviders(<WaterPage />, { token: WORKER });

    expect(screen.getByText("Loading…")).toBeInTheDocument();
    await screen.findByText("morning"); // list resolved → placeholder gone
    expect(screen.queryByText("Loading…")).not.toBeInTheDocument();
  });

  it("renders each record: amount+unit, source, flock name, and meter delta vs em-dash", async () => {
    mockListWaterUsage.mockResolvedValue([ROW, METER_ROW]);
    renderWithProviders(<WaterPage />, { token: WORKER });

    const row1 = await screen.findByRole("row", { name: /2026-07-10/ });
    expect(within(row1).getByText("12 L")).toBeInTheDocument();
    expect(within(row1).getByText("Well")).toBeInTheDocument();
    expect(within(row1).getByText("Hen House 1")).toBeInTheDocument();
    expect(within(row1).getByText("—")).toBeInTheDocument(); // direct quantity → no meters

    const row2 = screen.getByRole("row", { name: /2026-07-11/ });
    expect(within(row2).getByText("74.75 L")).toBeInTheDocument();
    expect(within(row2).getByText("100.5 → 175.25")).toBeInTheDocument(); // meter delta shown
  });

  it("shows the empty state when no records match", async () => {
    mockListWaterUsage.mockResolvedValue([]);
    renderWithProviders(<WaterPage />, { token: WORKER });

    expect(await screen.findByText("No water records match.")).toBeInTheDocument();
  });
});

describe("WaterPage record water", () => {
  it("records direct-quantity usage: full body (non-default source/unit/qty/flock, trimmed note) + key", async () => {
    mockRecordWaterUsage.mockResolvedValue({ id: "w9" });
    await renderReadyForm(WORKER);

    fireEvent.change(screen.getByLabelText("Source"), { target: { value: "Municipal" } });
    fireEvent.change(screen.getByLabelText("Unit"), { target: { value: "gal" } });
    fireEvent.change(screen.getByLabelText(/Quantity/), { target: { value: "12.5" } });
    fireEvent.change(screen.getByLabelText("Note"), { target: { value: "  morning top-up  " } });
    // first "Flock" combobox is the capture picker (the second is the list filter)
    fireEvent.change(screen.getAllByLabelText("Flock")[0], { target: { value: "f2" } });

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Record water" }));
    });

    expect(await screen.findByText("Water recorded.")).toBeInTheDocument();
    const [body, key] = mockRecordWaterUsage.mock.calls[0];
    expect(body).toEqual({
      source: "Municipal", unit: "gal", quantity: 12.5,
      note: "morning top-up", flockId: "f2", date: todayIso(),
    });
    expect(body.meterStart).toBeUndefined(); // direct quantity → no meter fields sent
    expect(body.meterEnd).toBeUndefined();
    expect(key).toEqual(expect.any(String)); // idempotency key
  });

  it("records meter readings: sends meterStart/meterEnd and omits quantity", async () => {
    mockRecordWaterUsage.mockResolvedValue({ id: "w9" });
    await renderReadyForm(WORKER);

    fireEvent.click(screen.getByRole("checkbox")); // "from meter readings"
    fireEvent.change(screen.getByLabelText("Meter start"), { target: { value: "100.5" } });
    fireEvent.change(screen.getByLabelText("Meter end"), { target: { value: "175.25" } });

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Record water" }));
    });

    expect(await screen.findByText("Water recorded.")).toBeInTheDocument();
    const [body] = mockRecordWaterUsage.mock.calls[0];
    expect(body).toEqual({
      source: "Well", unit: "L", meterStart: 100.5, meterEnd: 175.25,
      flockId: "f1", date: todayIso(),
    });
    expect(body.quantity).toBeUndefined(); // meter mode → amount derives server-side
  });

  it("rejects a non-positive quantity client-side and does not call the API", async () => {
    await renderReadyForm(WORKER);

    fireEvent.change(screen.getByLabelText(/Quantity/), { target: { value: "0" } });
    // Submit the form directly: jsdom otherwise blocks the submit-button click on
    // the HTML min-constraint (0.001) before onSubmit's own positive-number guard
    // can run — dispatching submit bypasses interactive constraint validation.
    const form = screen.getByRole("button", { name: "Record water" }).closest("form")!;
    await act(async () => { fireEvent.submit(form); });

    expect(screen.getByText("Quantity must be a positive number.")).toBeInTheDocument();
    expect(mockRecordWaterUsage).not.toHaveBeenCalled(); // guard short-circuits the write
  });

  it("replays the same idempotency key after a failed record, and rotates it after success", async () => {
    mockRecordWaterUsage.mockRejectedValueOnce(new ApiError(500, "Server error", "boom"));
    mockRecordWaterUsage.mockResolvedValue({ id: "w9" });
    await renderReadyForm(WORKER);
    const qty = () => screen.getByLabelText(/Quantity/);
    const recordBtn = () => screen.getByRole("button", { name: "Record water" });

    fireEvent.change(qty(), { target: { value: "5" } });
    await act(async () => { fireEvent.click(recordBtn()); });
    expect(await screen.findByText(/boom/)).toBeInTheDocument(); // failure kept the key

    // same flock + date → same key scope; resetForm hasn't run (only on success)
    fireEvent.change(qty(), { target: { value: "5" } });
    await act(async () => { fireEvent.click(recordBtn()); });
    await screen.findByText("Water recorded."); // success cleared the key

    fireEvent.change(qty(), { target: { value: "7" } });
    await act(async () => { fireEvent.click(recordBtn()); });

    const k1 = mockRecordWaterUsage.mock.calls[0][1];
    const k2 = mockRecordWaterUsage.mock.calls[1][1];
    const k3 = mockRecordWaterUsage.mock.calls[2][1];
    expect(k2).toBe(k1); // failure replayed the exact key
    expect(k3).not.toBe(k2); // success rotated it → next write is fresh
  });
});

describe("WaterPage correct (edit/update)", () => {
  it("sends a version-guarded body (the loaded version) + key, locks flock/date, then resets", async () => {
    mockListWaterUsage.mockResolvedValue([ROW]);
    mockUpdateWaterUsage.mockResolvedValue(undefined);
    renderWithProviders(<WaterPage />, { token: ADMIN });

    fireEvent.click(await screen.findByRole("button", { name: "correct" }));

    // flock + date are fixed once recorded → the capture pickers lock in edit mode
    expect(screen.getAllByLabelText("Flock")[0]).toBeDisabled();
    expect(screen.getByLabelText("Date")).toBeDisabled();
    const saveBtn = screen.getByRole("button", { name: "Save correction" });

    fireEvent.change(screen.getByLabelText("Source"), { target: { value: "Municipal" } });
    fireEvent.change(screen.getByLabelText(/Quantity/), { target: { value: "18.5" } });
    fireEvent.change(screen.getByLabelText("Note"), { target: { value: "recount" } });

    await act(async () => { fireEvent.click(saveBtn); });

    expect(await screen.findByText("Water record corrected.")).toBeInTheDocument();
    const [id, body, key] = mockUpdateWaterUsage.mock.calls[0];
    expect(id).toBe("w1");
    expect(body).toEqual({
      source: "Municipal", unit: "L", quantity: 18.5, note: "recount", version: 3,
    });
    expect(body.version).toBe(3); // base version sent → a stale edit 409s server-side
    expect(body.meterStart).toBeUndefined();
    expect(body.meterEnd).toBeUndefined();
    expect(key).toEqual(expect.any(String)); // idempotency key

    // success returns the form to capture mode
    expect(screen.getByRole("button", { name: "Record water" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Save correction" })).not.toBeInTheDocument();
  });
});

describe("WaterPage role gating — correcting is admin-only, recording is open", () => {
  // isAdmin = Admin || Manager (claims.ts). Everyone else — including a plain
  // Worker with no role claim — sees no "correct" control but can still record.
  it.each([
    { label: "Admin", token: { sub: "u1", role: "Admin" }, canCorrect: true },
    { label: "Manager", token: { sub: "u1", role: "Manager" }, canCorrect: true },
    { label: "Sales", token: { sub: "u1", role: "Sales" }, canCorrect: false },
    { label: "ReadOnly", token: { sub: "u1", role: "ReadOnly" }, canCorrect: false },
    { label: "Worker (no role claim)", token: { sub: "u1" }, canCorrect: false },
  ])("$label — correct button shown: $canCorrect (record form always present)", async ({ token, canCorrect }) => {
    mockListWaterUsage.mockResolvedValue([ROW]);
    renderWithProviders(<WaterPage />, { token });

    await screen.findByText("morning"); // the record row rendered
    const correct = screen.queryByRole("button", { name: "correct" });
    if (canCorrect) expect(correct).toBeInTheDocument();
    else expect(correct).not.toBeInTheDocument();
    // recording is open to everyone regardless of role
    expect(screen.getByRole("button", { name: "Record water" })).toBeInTheDocument();
  });
});

describe("WaterPage list filter", () => {
  it("re-queries water usage scoped to the selected flock filter", async () => {
    await renderReadyForm(WORKER);

    // mount load: unscoped, first page of PAGE=50
    expect(mockListWaterUsage).toHaveBeenCalledWith(
      expect.objectContaining({ flockId: undefined, limit: 50, offset: 0 }),
    );

    // second "Flock" combobox is the list filter
    fireEvent.change(screen.getAllByLabelText("Flock")[1], { target: { value: "f2" } });

    await waitFor(() =>
      expect(mockListWaterUsage).toHaveBeenCalledWith(
        expect.objectContaining({ flockId: "f2", offset: 0 }),
      ),
    );
  });
});
