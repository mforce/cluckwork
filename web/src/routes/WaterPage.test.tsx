import { describe, it, expect, vi, beforeEach } from "vitest";
import { screen, within, fireEvent, waitFor, act } from "@testing-library/react";
import { WaterPage } from "./WaterPage";
import { renderWithProviders } from "../test/renderWithProviders";
import {
  listFlocks, listWaterUsage, recordWaterUsage, updateWaterUsage, getFlock,
} from "../api/cluckwork";
import type { Flock, WaterUsage } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { todayIso } from "../lib/dates";
import i18n from "../i18n";
import { NO_RECORD_HISTORY } from "../test/fixtures";

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
  getFlock: vi.fn(),
  getCustomer: vi.fn(),
}));

const mockListFlocks = vi.mocked(listFlocks);
const mockListWaterUsage = vi.mocked(listWaterUsage);
const mockRecordWaterUsage = vi.mocked(recordWaterUsage);
const mockUpdateWaterUsage = vi.mocked(updateWaterUsage);

const FLOCK_A: Flock = {
  ...NO_RECORD_HISTORY,
  id: "f1", farmId: "farm1", houseId: "h1", name: "Hen House 1", breed: "ISA",
  placementDate: "2026-01-01", initialCount: 100, currentBirds: 98, status: "Active",
};
const FLOCK_B: Flock = { ...FLOCK_A, id: "f2", name: "Coop 2" };

// Direct-quantity record (meterStart null → the "—" meters cell, edit fills the
// quantity field). version 3 is the base a correction must send back.
const ROW: WaterUsage = {
  id: "w1", flockId: "f1", flockName: "Hen House 1", date: "2026-07-10", quantity: 12, unit: "L",
  source: "Well", meterStart: null, meterEnd: null, note: "morning", version: 3,
  dailyEntryId: null,
};
// Meter-backed record: quantity is the delta, and the meters cell renders it.
const METER_ROW: WaterUsage = {
  id: "w2", flockId: "f1", flockName: "Hen House 1", date: "2026-07-11", quantity: 74.75, unit: "L",
  source: "Municipal", meterStart: 100.5, meterEnd: 175.25, note: null, version: 1,
  dailyEntryId: null,
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

    const row1 = await screen.findByRole("row", { name: /07\/10\/2026/ });
    expect(within(row1).getByText("12 L")).toBeInTheDocument();
    expect(within(row1).getByText("Well")).toBeInTheDocument();
    expect(within(row1).getByText("Hen House 1")).toBeInTheDocument();
    expect(within(row1).getByText("—")).toBeInTheDocument(); // direct quantity → no meters

    const row2 = screen.getByRole("row", { name: /07\/11\/2026/ });
    expect(within(row2).getByText("74.75 L")).toBeInTheDocument();
    expect(within(row2).getByText("100.5 → 175.25")).toBeInTheDocument(); // meter delta shown
  });

  it("shows the empty state when no records match", async () => {
    mockListWaterUsage.mockResolvedValue([]);
    renderWithProviders(<WaterPage />, { token: WORKER });

    expect(await screen.findByText("No water records match.")).toBeInTheDocument();
  });

  // #512 US4 (T043/T051) — a record row's own flockName is null (the flock
  // left the caller's tenant/flock scope between reads), even though the
  // SAME id is present in the page's own capped `flocks` list under a
  // DIFFERENT-looking name. The row must show the translated unavailable
  // label, never that catalog substitution and never a raw id fragment.
  it("a record row whose own flockName is null shows the translated unavailable label — never the catalog's name for that id, never an id fragment", async () => {
    mockListWaterUsage.mockResolvedValue([{ ...ROW, id: "w-gone", flockId: "f1", flockName: null }]);
    renderWithProviders(<WaterPage />, { token: WORKER });

    const dataRow = await screen.findByRole("row", { name: /morning/ });
    expect(within(dataRow).getByText(i18n.t("water:rowFlockUnavailable"))).toBeInTheDocument();
    expect(within(dataRow).queryByText("Hen House 1")).not.toBeInTheDocument();
    expect(within(dataRow).queryByText("f1")).not.toBeInTheDocument();
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
    // #512 — the capture flock is a FlockPicker: open the trigger, commit the
    // option by pointer.
    fireEvent.click(screen.getByRole("button", { name: /Hen House 1/ }));
    fireEvent.click(await screen.findByRole("option", { name: "Coop 2" }));

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

    // flock + date are fixed once recorded → the capture pickers lock in edit
    // mode; the row-owned flock value is preserved exactly (T037).
    expect(screen.getByRole("button", { name: /Hen House 1/ })).toBeDisabled();
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
  it("re-queries with the full flock + from/to filter, resetting to the first page", async () => {
    await renderReadyForm(WORKER);

    // mount load: unscoped, first page of PAGE=50
    expect(mockListWaterUsage).toHaveBeenCalledWith(
      expect.objectContaining({ flockId: undefined, from: undefined, to: undefined, limit: 50, offset: 0 }),
    );

    // Drive all three filters. The date inputs are queried by their <label> text
    // (accessible name), not a fragile positional index, so a broken from/to
    // propagation can't slip past a flockId-only assertion.
    // #512 — the filter flock is now a FlockPicker: open the trigger ("All"),
    // commit the option, and the list re-queries with the committed id.
    fireEvent.click(screen.getByRole("button", { name: /All/ }));
    fireEvent.click(await screen.findByRole("option", { name: "Coop 2" }));
    fireEvent.change(screen.getByLabelText("From"), { target: { value: "2026-07-01" } });
    fireEvent.change(screen.getByLabelText("To"), { target: { value: "2026-07-31" } });

    // The complete request shape must carry every filter — full match, no
    // objectContaining, because load() sends exactly these five keys.
    await waitFor(() =>
      expect(mockListWaterUsage).toHaveBeenCalledWith({
        flockId: "f2", from: "2026-07-01", to: "2026-07-31", limit: 50, offset: 0,
      }),
    );
  });
});

describe("WaterPage pagination", () => {
  it("appends the next page and re-queries at offset 50 when 'load more' is clicked", async () => {
    // A full page (PAGE=50) sets hasMore → the "load more" button renders. The
    // first row of page one and the sole row of page two carry unique notes so
    // we can prove page two is APPENDED to page one, not swapped in for it.
    const page1: WaterUsage[] = Array.from({ length: 50 }, (_, i) => ({
      ...ROW, id: `p1-${i}`, note: i === 0 ? "first-page-row" : `row-${i}`,
    }));
    const page2: WaterUsage[] = [{ ...ROW, id: "p2-0", note: "second-page-row" }];
    mockListWaterUsage.mockResolvedValueOnce(page1);
    mockListWaterUsage.mockResolvedValueOnce(page2);

    renderWithProviders(<WaterPage />, { token: WORKER });
    await screen.findByText("first-page-row"); // page one landed

    // mount load fetched the first page at offset 0
    expect(mockListWaterUsage).toHaveBeenCalledWith(
      expect.objectContaining({ limit: 50, offset: 0 }),
    );

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "load more" }));
    });

    // the next page is fetched at offset 50 — the current row count, not a reset
    await waitFor(() =>
      expect(mockListWaterUsage).toHaveBeenCalledWith(
        expect.objectContaining({ limit: 50, offset: 50 }),
      ),
    );

    // both pages' rows are on screen → page two was appended, not substituted
    expect(screen.getByText("second-page-row")).toBeInTheDocument();
    expect(screen.getByText("first-page-row")).toBeInTheDocument();
  });
});

// ---------------------------------------------------------------------------
// i18n wiring (#182, Task 13, batch B2)
// ---------------------------------------------------------------------------

// `water` is English-only (not in TRANSLATED_NAMESPACES — see
// translations-status.ts), so under ANY UI language the rendered text falls
// back to this exact English string, same as a still-hardcoded literal would
// render — asserting it, even under a non-English locale, would prove nothing
// (CONTRIBUTING-i18n.md's fallback trap). Swap the catalog value at runtime
// instead, the same i18n.addResource technique the other batches use, so each
// marker only renders if the screen actually reads the catalog rather than a
// literal that happens to still match it.
describe("WaterPage i18n wiring (#182, Task 13)", () => {
  function withOverride(ns: string, key: string, value: string, run: () => Promise<void> | void) {
    const original = i18n.getResource("en", ns, key) as string;
    i18n.addResource("en", ns, key, value);
    return Promise.resolve(run()).finally(() => {
      i18n.addResource("en", ns, key, original);
    });
  }

  it("reads the heading from the catalog, not a hardcoded literal", async () => {
    await withOverride("water", "title", "TITLE-MARKER", async () => {
      renderWithProviders(<WaterPage />, { token: WORKER });
      expect(await screen.findByRole("heading", { name: "TITLE-MARKER" })).toBeInTheDocument();
      expect(screen.queryByRole("heading", { name: "Water" })).not.toBeInTheDocument();
    });
  });

  it("reads the record-water button label from the catalog, not a hardcoded literal", async () => {
    await withOverride("water", "recordWaterButton", "RECORD-MARKER", async () => {
      renderWithProviders(<WaterPage />, { token: WORKER });
      await waitFor(() =>
        expect(screen.getByRole("button", { name: "RECORD-MARKER" })).toBeEnabled());
      expect(screen.queryByRole("button", { name: "Record water" })).not.toBeInTheDocument();
    });
  });

  // Proves the quantity label reads BOTH the catalog template AND the
  // enum-labelled (waterUnitLabel) current unit — a hardcoded literal, or one
  // that interpolated the raw wire value instead of the label, would still
  // pass a naive check since "L" is its own identity label, but would fail to
  // pick up the catalog marker text at all.
  it("interpolates the enum-labelled unit into the quantity label from the catalog", async () => {
    await withOverride(
      "water", "quantityLabelWithUnit", "QTY-MARKER {{unit}} MARKER-END",
      async () => {
        renderWithProviders(<WaterPage />, { token: WORKER });
        expect(await screen.findByLabelText("QTY-MARKER L MARKER-END")).toBeInTheDocument();
        expect(screen.queryByLabelText(/^Quantity/)).not.toBeInTheDocument();
      },
    );
  });

  it("reads the positive-quantity validation message from the catalog, not a hardcoded literal", async () => {
    await withOverride("water", "quantityMustBePositive", "QTY-ERROR-MARKER", async () => {
      await renderReadyForm(WORKER);
      fireEvent.change(screen.getByLabelText(/Quantity/), { target: { value: "0" } });
      const form = screen.getByRole("button", { name: "Record water" }).closest("form")!;
      await act(async () => { fireEvent.submit(form); });
      expect(screen.getByText("QTY-ERROR-MARKER")).toBeInTheDocument();
      expect(screen.queryByText("Quantity must be a positive number.")).not.toBeInTheDocument();
    });
  });

  // The success message is built with the imperative i18n.t() (onSubmit is an
  // event handler, not render — see CONTRIBUTING-i18n.md).
  it("reads the recorded success message from the catalog, not a hardcoded literal", async () => {
    mockRecordWaterUsage.mockResolvedValue({ id: "w9" });
    await withOverride("water", "recordedMessage", "RECORDED-MARKER", async () => {
      await renderReadyForm(WORKER);
      fireEvent.change(screen.getByLabelText(/Quantity/), { target: { value: "5" } });
      await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: "Record water" }));
      });
      expect(await screen.findByText("RECORDED-MARKER")).toBeInTheDocument();
      expect(screen.queryByText("Water recorded.")).not.toBeInTheDocument();
    });
  });
});

// #512 (T031/T037) — lifecycle: the row-owned capture identity of a disabled
// edit must be EXACT (the row's real flock, resolved through the exact GET
// when the loaded list does not carry it — never a fabricated splice of the
// row's name onto another flock), and reset must start a FRESH default.
describe("WaterPage lifecycle (#512 T031/T037)", () => {
  // An Archived flock the active/depleted discovery window never carries —
  // only the full list (includeArchived) and the exact GET know it.
  const ARCHIVED: Flock = {
    ...FLOCK_A, id: "fx", name: "Old Coop", breed: "Lohmann",
    currentBirds: 0, status: "Archived",
  };
  // Distinct metadata so a fabricated entity is impossible to confuse with
  // the row's own flock: every field but id/date differs from FLOCK_A.
  const ARCHIVED_ROW: WaterUsage = {
    id: "wArch", flockId: "fx", flockName: "Old Coop", date: "2026-07-05",
    quantity: 30, unit: "L", source: "Tank", meterStart: null, meterEnd: null,
    note: "last haul", version: 2, dailyEntryId: null,
  };

  // i18n-derived trigger selector: the capture flock trigger's accessible name
  // is "<flockLabel> <flockName>" (label + value span via aria-labelledby).
  const flockTriggerName = (flockName: string | null) => new RegExp(
    `${i18n.t("water:flockLabel").replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}.*${flockName}`,
  );

  // The row's flock IS in the page's loaded (includeArchived) list: startEdit
  // must admit that FULL loaded object as-is — no exact GET (an id-only or
  // fabricated path would have requested one) and no spliced entity. The test
  // waits for the includeArchived list to resolve before clicking correct, so
  // startEdit sees the populated flocks array.
  it("edit of a row whose flock is in the loaded list admits that full entity as-is — no exact GET, no fabricated splice", async () => {
    mockListWaterUsage.mockResolvedValue([ARCHIVED_ROW]);
    // The page's includeArchived load carries both the active default and the
    // row's archived flock. No filler needed — two flocks suffice.
    mockListFlocks.mockImplementation(async (p?: { offset?: number; limit?: number; includeArchived?: boolean }) => {
      const all = [FLOCK_A, ARCHIVED];
      if (p?.includeArchived) return all;
      // Picker discovery (active-and-depleted): only the Active flock.
      return [FLOCK_A];
    });
    renderWithProviders(<WaterPage />, { token: ADMIN });

    // Wait for BOTH mount loads to settle: the water list (row visible) AND
    // the flock list (flocks state populated). The "correct" button appears
    // when the water list resolves; the trigger's default flock name appears
    // when the flock list resolves. Waiting for the default trigger proves
    // the includeArchived list has landed.
    const correctBtn = await screen.findByRole("button", { name: i18n.t("water:correctButton") });
    // The capture trigger shows the default (first Active) flock once the
    // flock list resolves — this is the signal that flocks state is set.
    await waitFor(() => {
      expect(screen.getByRole("button", { name: flockTriggerName(FLOCK_A.name) })).toBeInTheDocument();
    });

    // Now click correct: startEdit sees flocks populated, finds ARCHIVED by
    // id, and commits the FULL loaded entity as-is.
    fireEvent.click(correctBtn);

    // The trigger renders the row's own flock name (the loaded entity's name)
    // (accessible name = label + value, so the catalog label leads it)
    const trigger = await screen.findByRole("button", { name: flockTriggerName(ARCHIVED_ROW.flockName) });
    // … without the depleted suffix — a fabricated Active/Depleted-shaped
    // entity wearing that name is ruled out — and the control is disabled:
    // a disabled edit preserves the exact row-owned value, cannot re-select.
    expect(trigger).not.toHaveTextContent(i18n.t("water:depletedFlockSuffix"));
    expect(trigger).toBeDisabled();

    // The FULL loaded entity was admitted as-is: no exact read was issued, and
    // the save guard reads the committed entity, not a placeholder.
    expect(vi.mocked(getFlock)).not.toHaveBeenCalled();
    expect(screen.getByRole("button", { name: i18n.t("water:saveCorrectionButton") })).toBeEnabled();
  });

  // The row's flock is NOT in the page's loaded list at all (it fell off the
  // archived window): startEdit must pass its id through the exact GET, keep
  // captureFlock null (NO fabricated entity), and show only the row's own
  // flockName while the read is in flight. The late list resolution must NOT
  // replace the row-owned transition; reset restores the active default; and
  // a held exact GET resolving late cannot resurrect the row's flock.
  it("a late list load after an off-list edit cannot replace the row-owned transition, and reset restores the fresh default", async () => {
    mockListWaterUsage.mockResolvedValue([ARCHIVED_ROW]);
    // The page's includeArchived load returns ONLY the active default — the
    // row's archived flock is absent, so startEdit takes the requestedId
    // exact-GET path. Both reads are deferred: the page's list load must land
    // AFTER the off-list transition (its setFlocks + default commit used to
    // overwrite exactly that transition), and the exact GET is held so the
    // RESET, not its landing, decides the end state.
    let releaseFlocks!: (all: Flock[]) => void;
    mockListFlocks.mockImplementation(() => new Promise<Flock[]>((r) => { releaseFlocks = r; }));
    const getFlockMock = vi.mocked(getFlock);
    let resolveExact!: (f: Flock) => void;
    getFlockMock.mockImplementation(async () => new Promise<Flock>((r) => { resolveExact = r; }));

    renderWithProviders(<WaterPage />, { token: ADMIN });
    // The row is already on screen (the water list resolves independently);
    // the flock list is still held — click correct while it is unknown.
    fireEvent.click(await screen.findByRole("button", { name: i18n.t("water:correctButton") }));

    // The off-list transition is committed: the exact GET was requested for
    // the row-owned id, exactly once.
    expect(getFlockMock).toHaveBeenCalledTimes(1);
    expect(getFlockMock).toHaveBeenCalledWith("fx"); // the row-owned id, exactly

    // The LATE page list load now resolves (still without the row's flock).
    // Without the guard, its mount default would replace the row-owned
    // off-list transition with the first Active flock here.
    await act(async () => { releaseFlocks([FLOCK_A]); });

    // The row-owned transition SURVIVES the late list load: the trigger still
    // shows the row's own flockName (display-only) — not the default's.
    expect(screen.getByRole("button", {
      name: flockTriggerName(ARCHIVED_ROW.flockName),
    })).toBeInTheDocument();

    // Cancel the edit BEFORE the exact GET settles.
    fireEvent.click(screen.getByRole("button", { name: i18n.t("water:cancelEditButton") }));

    // The committed capture is now a FRESH active/default generation — the
    // default flock, not the row's flock (the row's display-only name is
    // gone with editingId cleared).
    const defaultName = FLOCK_A.name;
    expect(await screen.findByRole("button", { name: flockTriggerName(defaultName) })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: flockTriggerName(ARCHIVED_ROW.flockName) })).not.toBeInTheDocument();
    // Form is genuinely in capture mode again.
    expect(screen.getByRole("button", { name: i18n.t("water:recordWaterButton") })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: i18n.t("water:saveCorrectionButton") })).not.toBeInTheDocument();

    // Now the held exact GET settles: a superseded transition (the reset
    // bumped the controlled generation AND dropped the page's requestedId)
    // must NOT commit the row's flock over the fresh default.
    await act(async () => { resolveExact(ARCHIVED); });
    expect(screen.getByRole("button", { name: flockTriggerName(defaultName) })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: flockTriggerName(ARCHIVED_ROW.flockName) })).not.toBeInTheDocument();
  });

  // #512 US3 remediation — the capture picker is DISABLED during an edit
  // (`disabled={editingId !== null}`) AND its trigger is itself disabled, so
  // the user cannot open it to see the generic engine's (pre-fix) open-only
  // unavailable/Retry. Before the fix, a row-owned id whose exact GET failed
  // during an edit had NO recovery at all. Now the translated unavailable
  // status and a GET-only Retry render adjacent to the trigger regardless.
  it("an off-list row-owned flock whose exact GET fails during a DISABLED edit still shows unavailable + adjacent Retry — Save is blocked, Retry is GET-only and can recover", async () => {
    mockListWaterUsage.mockResolvedValue([ARCHIVED_ROW]);
    // "fx" (the row's flock) is absent from BOTH lists — startEdit must take
    // the requestedId exact-GET path.
    mockListFlocks.mockResolvedValue([FLOCK_A]);
    const getFlockMock = vi.mocked(getFlock);
    getFlockMock.mockRejectedValueOnce(new Error("not found"));

    renderWithProviders(<WaterPage />, { token: ADMIN });
    await waitFor(() => {
      expect(screen.getByRole("button", { name: flockTriggerName(FLOCK_A.name) })).toBeInTheDocument();
    });
    fireEvent.click(await screen.findByRole("button", { name: i18n.t("water:correctButton") }));

    // The exact GET was issued for the row-owned id.
    await waitFor(() => expect(getFlockMock).toHaveBeenCalledWith("fx"));
    // The trigger stays disabled (edit-locked) and shows only the row's own
    // flockName — never a fabricated entity, never the default.
    const trigger = screen.getByRole("button", { name: flockTriggerName(ARCHIVED_ROW.flockName) });
    expect(trigger).toBeDisabled();

    // Adjacent recovery renders even though the picker is disabled and was
    // never opened.
    const unavailableLabel = i18n.t("namedEntityPicker:unavailable");
    await waitFor(() => expect(screen.getByRole("alert")).toHaveTextContent(unavailableLabel));
    const retryLabel = i18n.t("namedEntityPicker:retry");
    const retryBtn = screen.getByRole("button", { name: retryLabel });

    // Save is withheld while unavailable.
    const save = screen.getByRole("button", { name: i18n.t("water:saveCorrectionButton") });
    expect(save).toBeDisabled();

    // Retry is exempt from `disabled` — it re-resolves the FIXED identity —
    // and repeats ONLY the exact GET, never a write.
    getFlockMock.mockResolvedValueOnce(ARCHIVED);
    fireEvent.click(retryBtn);
    await waitFor(() => expect(getFlockMock).toHaveBeenCalledTimes(2));
    expect(mockUpdateWaterUsage).not.toHaveBeenCalled();
    await waitFor(() => expect(screen.getByRole("button", { name: i18n.t("water:saveCorrectionButton") })).toBeEnabled());
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });
});

describe("WaterPage list races (#469)", () => {
  // This list had NO request sequencing: two quick filter picks let the older
  // response win, and a stale rejection painted an error over a healthy view.
  it("ignores a stale filter response that lands after a newer one", async () => {
    mockListWaterUsage.mockResolvedValue([ROW]);
    await renderReadyForm(ADMIN);
    expect(screen.getByText("morning")).toBeInTheDocument();

    let releaseStale!: (rows: WaterUsage[]) => void;
    mockListWaterUsage.mockReturnValueOnce(new Promise((r) => { releaseStale = r; }));
    fireEvent.change(screen.getByLabelText("From"), { target: { value: "2026-07-01" } });
    mockListWaterUsage.mockResolvedValueOnce([{ ...ROW, id: "wF", note: "fresh rows" }]);
    await act(async () => {
      fireEvent.change(screen.getByLabelText("To"), { target: { value: "2026-07-31" } });
    });
    expect(screen.getByText("fresh rows")).toBeInTheDocument();

    await act(async () => {
      releaseStale([{ ...ROW, id: "wS", note: "stale rows" }]);
    });
    expect(screen.getByText("fresh rows")).toBeInTheDocument();
    expect(screen.queryByText("stale rows")).not.toBeInTheDocument();
  });

  it("discards a stale rejection instead of painting an error over a healthy list", async () => {
    mockListWaterUsage.mockResolvedValue([ROW]);
    await renderReadyForm(ADMIN);

    let rejectStale!: (err: Error) => void;
    mockListWaterUsage.mockReturnValueOnce(new Promise((_, rej) => { rejectStale = rej; }));
    fireEvent.change(screen.getByLabelText("From"), { target: { value: "2026-07-01" } });
    mockListWaterUsage.mockResolvedValueOnce([{ ...ROW, id: "wF", note: "fresh rows" }]);
    await act(async () => {
      fireEvent.change(screen.getByLabelText("To"), { target: { value: "2026-07-31" } });
    });

    await act(async () => { rejectStale(new Error("stale blew up")); });
    expect(screen.getByText("fresh rows")).toBeInTheDocument();
    expect(screen.queryByText("Could not load water records.")).not.toBeInTheDocument();
  });
});
