import { describe, it, expect, vi, beforeEach } from "vitest";
import { screen, within, fireEvent, act } from "@testing-library/react";
import { FlocksPage } from "./FlocksPage";
import { renderWithProviders } from "../test/renderWithProviders";
import {
  archiveFlock, createFlock, depleteFlock, listBirdMovements, listFlocks,
  reactivateFlock, recordBirdMovement, updateFlock,
} from "../api/cluckwork";
import type { BirdMovement, Flock } from "../api/cluckwork";
import { ApiError } from "../api/client";

// Network seam only; ApiError stays real (errorMessage branches on it), and the
// date helpers (todayIso/ageWeeks) stay real too — they are pure and not mocked.
vi.mock("../api/cluckwork", () => ({
  listFlocks: vi.fn(),
  createFlock: vi.fn(),
  updateFlock: vi.fn(),
  recordBirdMovement: vi.fn(),
  depleteFlock: vi.fn(),
  archiveFlock: vi.fn(),
  reactivateFlock: vi.fn(),
  listBirdMovements: vi.fn(),
}));

const mockListFlocks = vi.mocked(listFlocks);
const mockCreate = vi.mocked(createFlock);
const mockUpdate = vi.mocked(updateFlock);
const mockRecordMovement = vi.mocked(recordBirdMovement);
const mockDeplete = vi.mocked(depleteFlock);
const mockArchive = vi.mocked(archiveFlock);
const mockReactivate = vi.mocked(reactivateFlock);
const mockListMovements = vi.mocked(listBirdMovements);

const ACTIVE: Flock = {
  id: "f1", farmId: "farm1", houseId: "h1", name: "Hen House 1", breed: "ISA Brown",
  placementDate: "2026-01-01", initialCount: 100, currentBirds: 98, status: "Active",
};
const DEPLETED: Flock = {
  id: "f2", farmId: "farm1", houseId: "h1", name: "Depleted Flock", breed: "Leghorn",
  placementDate: "2025-06-01", initialCount: 200, currentBirds: 0, status: "Depleted",
};
const ARCHIVED: Flock = {
  id: "f3", farmId: "farm1", houseId: "h1", name: "Old Coop", breed: "Sussex",
  placementDate: "2024-01-01", initialCount: 50, currentBirds: 0, status: "Archived",
};

const MOVEMENTS: BirdMovement[] = [
  { id: "m1", flockId: "f1", date: "2026-03-15", type: "Cull", quantity: 2, note: "sick" },
  { id: "m2", flockId: "f1", date: "2026-04-01", type: "Adjustment", quantity: -3, note: null },
];

const ADMIN = { sub: "u1", role: "Admin" };
const WORKER = { sub: "u1" };

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  // Mount only calls listFlocks; the rest fire on demand — give every seam a
  // safe default so an accidental call never returns undefined mid-render.
  mockListFlocks.mockResolvedValue([ACTIVE, DEPLETED]);
  mockCreate.mockResolvedValue({ id: "new" });
  mockUpdate.mockResolvedValue(undefined);
  mockRecordMovement.mockResolvedValue({ id: "mv-new" });
  mockDeplete.mockResolvedValue(undefined);
  mockArchive.mockResolvedValue(undefined);
  mockReactivate.mockResolvedValue(undefined);
  mockListMovements.mockResolvedValue([]);
  // deplete/archive are gated behind a confirm() dialog — default to accept.
  window.confirm = vi.fn(() => true);
});

// The mount-load error branch (listFlocks rejects → "Could not load flocks. Is
// the API up?") is intentionally not asserted: in this Vitest 3.2.7 + React 19
// stack a rejection the component *does* handle (its own .catch → setError) is
// still flagged as an unhandled rejection through an internal promise the test
// can't reach (vitest-dev/vitest #7940, #5796). The branch is a fixed message on
// any listFlocks rejection; the fetch transport is covered in api/client tests.

async function renderReady(token: Record<string, unknown>, flocks: Flock[] = [ACTIVE, DEPLETED]) {
  mockListFlocks.mockResolvedValue(flocks);
  renderWithProviders(<FlocksPage />, { token });
  // The create form only renders once the initial load resolves (flocks !== null).
  await screen.findByRole("button", { name: "Add flock" });
}

describe("FlocksPage loading + list", () => {
  it("shows a loading state until the flocks request resolves", async () => {
    let resolve!: (f: Flock[]) => void;
    mockListFlocks.mockReturnValue(new Promise<Flock[]>((r) => (resolve = r)));
    renderWithProviders(<FlocksPage />, { token: ADMIN });

    expect(screen.getByText(/Loading/)).toBeInTheDocument();
    resolve([]); // settle so the pending fetch doesn't dangle past the test
    await screen.findByText(/No flocks yet/);
  });

  it("shows the empty-state hint when there are no flocks", async () => {
    mockListFlocks.mockResolvedValue([]);
    renderWithProviders(<FlocksPage />, { token: ADMIN });
    expect(await screen.findByText(/No flocks yet/)).toBeInTheDocument();
  });

  it("renders a flock's current-vs-initial birds and status", async () => {
    await renderReady(ADMIN, [ACTIVE]);
    const row = screen.getByRole("row", { name: /Hen House 1/ });
    expect(within(row).getByText("98")).toBeInTheDocument(); // currentBirds
    expect(within(row).getByText("/ 100")).toBeInTheDocument(); // shown because 98 !== initialCount
    expect(within(row).getByText("Active")).toBeInTheDocument();
  });

  it("hides archived flocks until the show-archived toggle is checked", async () => {
    await renderReady(ADMIN, [ACTIVE, ARCHIVED]);
    // Archived hidden by default even though it was fetched (includeArchived).
    expect(screen.queryByText("Old Coop")).not.toBeInTheDocument();
    expect(mockListFlocks).toHaveBeenCalledWith({ includeArchived: true, limit: 500 });

    fireEvent.click(screen.getByRole("checkbox")); // "show 1 archived"
    expect(await screen.findByText("Old Coop")).toBeInTheDocument();
  });
});

describe("FlocksPage create", () => {
  it("creates a flock with the full form body and a key, then resets the name", async () => {
    mockCreate.mockResolvedValue({ id: "new" });
    await renderReady(ADMIN, [ACTIVE]);

    // Drive every field off its default (placed=today, count=100, name/breed="").
    fireEvent.change(screen.getByPlaceholderText("Name *"), { target: { value: "Rhode Reds" } });
    fireEvent.change(screen.getByPlaceholderText("Breed *"), { target: { value: "Rhode Island Red" } });
    fireEvent.change(screen.getByLabelText("Placed"), { target: { value: "2026-05-10" } });
    fireEvent.change(screen.getByLabelText("Birds"), { target: { value: "250" } });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Add flock" }));
    });

    expect(mockCreate.mock.calls[0][0]).toEqual({
      name: "Rhode Reds", breed: "Rhode Island Red", placementDate: "2026-05-10", initialCount: 250,
    });
    expect(mockCreate.mock.calls[0][1]).toEqual(expect.any(String)); // idempotency key
    expect(screen.getByPlaceholderText("Name *")).toHaveValue(""); // reset on success
  });
});

describe("FlocksPage edit", () => {
  it("saves an inline edit with the edited identity fields and a key", async () => {
    mockUpdate.mockResolvedValue(undefined);
    await renderReady(ADMIN, [ACTIVE]);

    // The <tr> node is stable across the switch to edit mode — capture it before
    // clicking, since the edit inputs would otherwise change its accessible name.
    const row = screen.getByRole("row", { name: /Hen House 1/ });
    fireEvent.click(within(row).getByRole("button", { name: "edit" }));

    // Query each edit field by its accessible name (aria-label), scoped to the
    // row — resilient to column reordering or added inputs, unlike DOM order /
    // getByDisplayValue. All four move off ACTIVE's seeded values (Hen House 1 /
    // ISA Brown / 2026-01-01 / 100) so the asserted body proves every field is
    // wired through, not just the ones that happened to change.
    fireEvent.change(within(row).getByRole("textbox", { name: "Edit name" }), { target: { value: "Barn A" } });
    fireEvent.change(within(row).getByLabelText("Edit breed"), { target: { value: "Hy-Line" } });
    fireEvent.change(within(row).getByLabelText("Edit placement date"), { target: { value: "2026-02-02" } });
    fireEvent.change(within(row).getByRole("spinbutton", { name: "Edit bird count" }), { target: { value: "120" } });
    await act(async () => {
      fireEvent.click(within(row).getByRole("button", { name: "save" }));
    });

    expect(mockUpdate.mock.calls[0][0]).toBe("f1");
    expect(mockUpdate.mock.calls[0][1]).toEqual({
      name: "Barn A", breed: "Hy-Line", placementDate: "2026-02-02", initialCount: 120,
    });
    expect(mockUpdate.mock.calls[0][2]).toEqual(expect.any(String)); // idempotency key
  });
});

describe("FlocksPage bird ledger", () => {
  it("opens a flock's movement ledger, calling listBirdMovements with the flock id, and renders signed rows", async () => {
    mockListMovements.mockResolvedValue(MOVEMENTS);
    await renderReady(ADMIN, [ACTIVE]);

    fireEvent.click(within(screen.getByRole("row", { name: /Hen House 1/ })).getByRole("button", { name: "birds" }));

    const cullRow = await screen.findByRole("row", { name: /Cull/ });
    expect(mockListMovements).toHaveBeenCalledWith("f1", { limit: 50 });
    expect(screen.getByRole("heading", { name: /Bird ledger — Hen House 1/ })).toBeInTheDocument();
    expect(within(cullRow).getByText("2026-03-15")).toBeInTheDocument();
    expect(within(cullRow).getByText("−2")).toBeInTheDocument(); // positive qty renders as a cull (−2)
    expect(within(cullRow).getByText("sick")).toBeInTheDocument();

    const adjRow = screen.getByRole("row", { name: /Adjustment/ });
    expect(within(adjRow).getByText("+3")).toBeInTheDocument(); // negative qty renders as a +3 correction
    expect(within(adjRow).getByText("—")).toBeInTheDocument(); // null note
  });

  it("records a bird movement with the type/quantity/date body and a key", async () => {
    mockListMovements.mockResolvedValue([]);
    mockRecordMovement.mockResolvedValue({ id: "mv9" });
    await renderReady(ADMIN, [ACTIVE]);

    fireEvent.click(within(screen.getByRole("row", { name: /Hen House 1/ })).getByRole("button", { name: "birds" }));

    // The create form AND the movement form both label an input "Birds"; scope
    // the field lookups to the movement form (the one owning the Record button).
    const recordBtn = await screen.findByRole("button", { name: "Record" });
    const form = recordBtn.closest("form")!;
    fireEvent.change(within(form).getByLabelText("Date"), { target: { value: "2026-05-01" } });
    fireEvent.change(within(form).getByRole("combobox"), { target: { value: "Adjustment" } }); // off "Cull" default
    fireEvent.change(within(form).getByLabelText("Birds"), { target: { value: "-5" } });
    fireEvent.change(within(form).getByPlaceholderText("Note"), { target: { value: "miscount" } });
    await act(async () => {
      fireEvent.click(recordBtn);
    });

    expect(mockRecordMovement.mock.calls[0][0]).toBe("f1");
    expect(mockRecordMovement.mock.calls[0][1]).toEqual({
      date: "2026-05-01", type: "Adjustment", quantity: -5, note: "miscount",
    });
    expect(mockRecordMovement.mock.calls[0][2]).toEqual(expect.any(String)); // idempotency key
  });
});

describe("FlocksPage lifecycle", () => {
  it("depletes an active flock after confirmation", async () => {
    mockDeplete.mockResolvedValue(undefined);
    await renderReady(ADMIN, [ACTIVE]);

    await act(async () => {
      fireEvent.click(within(screen.getByRole("row", { name: /Hen House 1/ })).getByRole("button", { name: "deplete" }));
    });

    expect(window.confirm).toHaveBeenCalled();
    expect(mockDeplete).toHaveBeenCalledWith("f1", expect.any(String));
  });

  it("does not deplete when the confirm dialog is cancelled", async () => {
    window.confirm = vi.fn(() => false);
    await renderReady(ADMIN, [ACTIVE]);

    await act(async () => {
      fireEvent.click(within(screen.getByRole("row", { name: /Hen House 1/ })).getByRole("button", { name: "deplete" }));
    });

    expect(window.confirm).toHaveBeenCalled();
    expect(mockDeplete).not.toHaveBeenCalled(); // confirm short-circuits the write
  });

  it("archives a non-archived flock after confirmation", async () => {
    mockArchive.mockResolvedValue(undefined);
    await renderReady(ADMIN, [ACTIVE, DEPLETED]);

    await act(async () => {
      fireEvent.click(within(screen.getByRole("row", { name: /Depleted Flock/ })).getByRole("button", { name: "archive" }));
    });

    expect(window.confirm).toHaveBeenCalled();
    expect(mockArchive).toHaveBeenCalledWith("f2", expect.any(String));
  });

  it("reactivates a depleted flock without a confirm dialog", async () => {
    mockReactivate.mockResolvedValue(undefined);
    await renderReady(ADMIN, [DEPLETED]);

    await act(async () => {
      fireEvent.click(within(screen.getByRole("row", { name: /Depleted Flock/ })).getByRole("button", { name: "reactivate" }));
    });

    expect(mockReactivate).toHaveBeenCalledWith("f2", expect.any(String));
    expect(window.confirm).not.toHaveBeenCalled(); // reactivate is the undo — no guard
  });
});

describe("FlocksPage idempotency", () => {
  it("replays the same create key after a failure, then rotates it after success", async () => {
    mockCreate.mockRejectedValueOnce(new ApiError(500, "Server error", "boom"));
    mockCreate.mockResolvedValue({ id: "new" });
    await renderReady(ADMIN, [ACTIVE]);
    const name = () => screen.getByPlaceholderText("Name *");
    const breed = () => screen.getByPlaceholderText("Breed *");

    fireEvent.change(name(), { target: { value: "One" } });
    fireEvent.change(breed(), { target: { value: "ISA" } });
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Add flock" })); });
    expect(await screen.findByText(/boom/)).toBeInTheDocument();

    // Failure kept the form values → the resubmit replays the same write.
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Add flock" })); });

    // Success cleared the form → refill for a genuinely fresh write.
    fireEvent.change(name(), { target: { value: "Two" } });
    fireEvent.change(breed(), { target: { value: "Hy-Line" } });
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Add flock" })); });

    const k1 = mockCreate.mock.calls[0][1];
    const k2 = mockCreate.mock.calls[1][1];
    const k3 = mockCreate.mock.calls[2][1];
    expect(k2).toBe(k1); // failure kept the key → exact replay
    expect(k3).not.toBe(k2); // success rotated it → the next write is fresh
  });
});

describe("FlocksPage role gating", () => {
  it("lets a worker add flocks but hides every lifecycle action", async () => {
    await renderReady(WORKER, [ACTIVE, DEPLETED]);

    // Creating a flock records the day's work — it is NOT admin-gated.
    expect(screen.getByRole("button", { name: "Add flock" })).toBeInTheDocument();

    const row = screen.getByRole("row", { name: /Hen House 1/ });
    expect(within(row).getByRole("button", { name: "birds" })).toBeInTheDocument(); // ledger read is open to all
    expect(within(row).queryByRole("button", { name: "edit" })).not.toBeInTheDocument();
    expect(within(row).queryByRole("button", { name: "deplete" })).not.toBeInTheDocument();
    expect(within(row).queryByRole("button", { name: "archive" })).not.toBeInTheDocument();

    const depletedRow = screen.getByRole("row", { name: /Depleted Flock/ });
    expect(within(depletedRow).queryByRole("button", { name: "reactivate" })).not.toBeInTheDocument();
  });

  it("shows a worker the ledger rows but no record form", async () => {
    mockListMovements.mockResolvedValue(MOVEMENTS);
    await renderReady(WORKER, [ACTIVE]);

    fireEvent.click(within(screen.getByRole("row", { name: /Hen House 1/ })).getByRole("button", { name: "birds" }));

    await screen.findByRole("row", { name: /Cull/ }); // rows render read-only
    expect(screen.queryByRole("button", { name: "Record" })).not.toBeInTheDocument();
    expect(screen.queryByRole("combobox")).not.toBeInTheDocument(); // no type picker → no record form
  });
});
