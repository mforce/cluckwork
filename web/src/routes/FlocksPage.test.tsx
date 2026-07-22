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
});

// F135: deplete/archive ask in the app's own dialog, not window.confirm, so the
// tests drive the real thing — click the trigger, then answer the question.
async function answer(name: string) {
  await act(async () => {
    fireEvent.click(await screen.findByRole("button", { name }));
  });
}

// The mount-load error branch (listFlocks rejects → "Could not load flocks. Is
// the API up?") is intentionally not asserted: in this Vitest 3.2.7 + React 19
// stack a rejection the component *does* handle (its own .catch → setError) is
// still flagged as an unhandled rejection through an internal promise the test
// can't reach (vitest-dev/vitest #7940, #5796). The branch is a fixed message on
// any listFlocks rejection; the fetch transport is covered in api/client tests.

async function renderReady(token: Record<string, unknown>, flocks: Flock[] = [ACTIVE, DEPLETED]) {
  mockListFlocks.mockResolvedValue(flocks);
  renderWithProviders(<FlocksPage />, { token });
  // The create action only renders once the initial load resolves (flocks !== null).
  await screen.findByRole("button", { name: "New flock" });
}

// F131: add/edit/record moved into dialogs — open first, same assertions after.
const openCreate = () => fireEvent.click(screen.getByRole("button", { name: "New flock" }));
const dialog = () => screen.getByRole("dialog");

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
    openCreate();

    // Drive every field off its default (placed=today, count=100, name/breed="").
    fireEvent.change(within(dialog()).getByLabelText("Name *"), { target: { value: "Rhode Reds" } });
    fireEvent.change(within(dialog()).getByLabelText("Breed *"), { target: { value: "Rhode Island Red" } });
    fireEvent.change(within(dialog()).getByLabelText("Placed"), { target: { value: "2026-05-10" } });
    fireEvent.change(within(dialog()).getByLabelText("Birds"), { target: { value: "250" } });
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Add flock" }));
    });

    expect(mockCreate.mock.calls[0][0]).toEqual({
      name: "Rhode Reds", breed: "Rhode Island Red", placementDate: "2026-05-10", initialCount: 250,
    });
    expect(mockCreate.mock.calls[0][1]).toEqual(expect.any(String)); // idempotency key
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument(); // success dismisses it
    openCreate();
    expect(within(dialog()).getByLabelText("Name *")).toHaveValue(""); // reset on success
  });
});

describe("FlocksPage edit", () => {
  it("saves an edit with the edited identity fields and a key", async () => {
    mockUpdate.mockResolvedValue(undefined);
    await renderReady(ADMIN, [ACTIVE]);

    const row = screen.getByRole("row", { name: /Hen House 1/ });
    fireEvent.click(within(row).getByRole("button", { name: "edit" }));

    // Query each edit field by its accessible name, scoped to the dialog. All
    // four move off ACTIVE's seeded values (Hen House 1 / ISA Brown /
    // 2026-01-01 / 100) so the asserted body proves every field is wired
    // through, not just the ones that happened to change.
    expect(within(dialog()).getByLabelText("Edit name")).toHaveValue("Hen House 1"); // seeded from the row
    fireEvent.change(within(dialog()).getByRole("textbox", { name: "Edit name" }), { target: { value: "Barn A" } });
    fireEvent.change(within(dialog()).getByLabelText("Edit breed"), { target: { value: "Hy-Line" } });
    fireEvent.change(within(dialog()).getByLabelText("Edit placement date"), { target: { value: "2026-02-02" } });
    fireEvent.change(within(dialog()).getByRole("spinbutton", { name: "Edit bird count" }), { target: { value: "120" } });
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Save" }));
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

    // The movement form lives in its own dialog, opened from the ledger panel.
    fireEvent.click(await screen.findByRole("button", { name: "Record movement" }));
    fireEvent.change(within(dialog()).getByLabelText("Date"), { target: { value: "2026-05-01" } });
    fireEvent.change(within(dialog()).getByLabelText("Type"), { target: { value: "Adjustment" } }); // off "Cull" default
    fireEvent.change(within(dialog()).getByLabelText("Birds"), { target: { value: "-5" } });
    fireEvent.change(within(dialog()).getByLabelText("Note"), { target: { value: "miscount" } });
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Record" }));
    });

    expect(mockRecordMovement.mock.calls[0][0]).toBe("f1");
    expect(mockRecordMovement.mock.calls[0][1]).toEqual({
      date: "2026-05-01", type: "Adjustment", quantity: -5, note: "miscount",
    });
    expect(mockRecordMovement.mock.calls[0][2]).toEqual(expect.any(String)); // idempotency key
  });
});

describe("FlocksPage dialog dismissal", () => {
  it("closes the create dialog on Cancel without writing", async () => {
    await renderReady(ADMIN, [ACTIVE]);
    openCreate();
    fireEvent.change(within(dialog()).getByLabelText("Name *"), { target: { value: "Abandoned" } });

    fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));

    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(mockCreate).not.toHaveBeenCalled();
  });

  it("closes the edit dialog on Cancel without writing", async () => {
    await renderReady(ADMIN, [ACTIVE]);
    fireEvent.click(within(screen.getByRole("row", { name: /Hen House 1/ })).getByRole("button", { name: "edit" }));

    fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));

    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(mockUpdate).not.toHaveBeenCalled();
  });

  it("closes the movement dialog on Cancel without writing", async () => {
    mockListMovements.mockResolvedValue([]);
    await renderReady(ADMIN, [ACTIVE]);
    fireEvent.click(within(screen.getByRole("row", { name: /Hen House 1/ })).getByRole("button", { name: "birds" }));
    fireEvent.click(await screen.findByRole("button", { name: "Record movement" }));

    fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));

    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(mockRecordMovement).not.toHaveBeenCalled();
  });
});

describe("FlocksPage lifecycle", () => {
  it("depletes an active flock after confirmation", async () => {
    mockDeplete.mockResolvedValue(undefined);
    await renderReady(ADMIN, [ACTIVE]);

    await act(async () => {
      fireEvent.click(within(screen.getByRole("row", { name: /Hen House 1/ })).getByRole("button", { name: "deplete" }));
    });

    // The question names the flock, so a mis-clicked row is visible before it
    // is too late — window.confirm's one-liner is now a titled dialog.
    expect(screen.getByRole("dialog")).toHaveAccessibleName('Deplete "Hen House 1"?');
    expect(mockDeplete).not.toHaveBeenCalled(); // nothing written until answered

    await answer("Deplete flock");
    expect(mockDeplete).toHaveBeenCalledWith("f1", expect.any(String));
  });

  it("does not deplete when the confirm dialog is cancelled", async () => {
    await renderReady(ADMIN, [ACTIVE]);

    await act(async () => {
      fireEvent.click(within(screen.getByRole("row", { name: /Hen House 1/ })).getByRole("button", { name: "deplete" }));
    });

    await answer("Cancel");
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(mockDeplete).not.toHaveBeenCalled(); // dismissal short-circuits the write
  });

  it("archives a non-archived flock after confirmation", async () => {
    mockArchive.mockResolvedValue(undefined);
    await renderReady(ADMIN, [ACTIVE, DEPLETED]);

    await act(async () => {
      fireEvent.click(within(screen.getByRole("row", { name: /Depleted Flock/ })).getByRole("button", { name: "archive" }));
    });

    expect(screen.getByRole("dialog")).toHaveAccessibleName('Archive "Depleted Flock"?');
    await answer("Archive flock");
    expect(mockArchive).toHaveBeenCalledWith("f2", expect.any(String));
  });

  it("reactivates a depleted flock without a confirm dialog", async () => {
    mockReactivate.mockResolvedValue(undefined);
    await renderReady(ADMIN, [DEPLETED]);

    await act(async () => {
      fireEvent.click(within(screen.getByRole("row", { name: /Depleted Flock/ })).getByRole("button", { name: "reactivate" }));
    });

    expect(mockReactivate).toHaveBeenCalledWith("f2", expect.any(String));
    expect(screen.queryByRole("dialog")).toBeNull(); // reactivate is the undo — no guard
  });
});

describe("FlocksPage idempotency", () => {
  it("replays the same create key after a failure, then rotates it after success", async () => {
    mockCreate.mockRejectedValueOnce(new ApiError(500, "Server error", "boom"));
    mockCreate.mockResolvedValue({ id: "new" });
    await renderReady(ADMIN, [ACTIVE]);
    openCreate();
    const name = () => within(dialog()).getByLabelText("Name *");
    const breed = () => within(dialog()).getByLabelText("Breed *");
    const submit = async () => {
      await act(async () => {
        fireEvent.click(within(dialog()).getByRole("button", { name: "Add flock" }));
      });
    };

    fireEvent.change(name(), { target: { value: "One" } });
    fireEvent.change(breed(), { target: { value: "ISA" } });
    await submit();
    // A failure keeps the dialog up, with the error inside it.
    expect(within(dialog()).getByText(/boom/)).toBeInTheDocument();

    // Failure kept the form values → the resubmit replays the same write.
    await submit();

    // Success closed the dialog and cleared the form → reopen and refill for a
    // genuinely fresh write.
    openCreate();
    fireEvent.change(name(), { target: { value: "Two" } });
    fireEvent.change(breed(), { target: { value: "Hy-Line" } });
    await submit();

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
    expect(screen.getByRole("button", { name: "New flock" })).toBeInTheDocument();

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
    // No way in: the action that opens the movement dialog is admin-only.
    expect(screen.queryByRole("button", { name: "Record movement" })).not.toBeInTheDocument();
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });
});
