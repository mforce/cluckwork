import { describe, it, expect, vi, beforeEach } from "vitest";
import { screen, within, fireEvent, act, waitFor, render, cleanup } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { FlocksPage } from "./FlocksPage";
import { getRowByCellText } from "../test/rows";
import { renderWithProviders } from "../test/renderWithProviders";
import { AuthContext } from "../auth/AuthContext";
import type { Role } from "../auth/claims";
import {
  archiveFlock, createFlock, depleteFlock, listBirdMovements, listFlocks,
  reactivateFlock, recordBirdMovement, updateFlock,
} from "../api/cluckwork";
import type { BirdMovement, Flock } from "../api/cluckwork";
import { ApiError } from "../api/client";
import i18n from "../i18n";
import { NO_RECORD_HISTORY, RECORD_HISTORY } from "../test/fixtures";

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
  getFlock: vi.fn(),
  getCustomer: vi.fn(),
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
  ...NO_RECORD_HISTORY,
  id: "f1", farmId: "farm1", houseId: "h1", name: "Hen House 1", breed: "ISA Brown",
  placementDate: "2026-01-01", initialCount: 100, currentBirds: 98, status: "Active",
};
const DEPLETED: Flock = {
  ...NO_RECORD_HISTORY,
  id: "f2", farmId: "farm1", houseId: "h1", name: "Depleted Flock", breed: "Leghorn",
  placementDate: "2025-06-01", initialCount: 200, currentBirds: 0, status: "Depleted",
};
const ARCHIVED: Flock = {
  ...NO_RECORD_HISTORY,
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

// A promise the test resolves by hand — holds a request open so the busy
// window is asserted deterministically, no timing guesses (client.test.ts idiom).
function deferred<T>() {
  let resolve!: (v: T) => void;
  const promise = new Promise<T>((r) => (resolve = r));
  return { promise, resolve };
}

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
  // Wait on a row instead of the New flock button: that button is now
  // admin-only, so a Worker fixture would never resolve. Some callers pass
  // only [DEPLETED], so pick whatever non-archived row the fixture has.
  const ready = flocks.find((f) => f.status !== "Archived")!;
  await screen.findByRole("row", { name: new RegExp(ready.name) });
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
    fireEvent.change(within(dialog()).getByRole("spinbutton", { name: "Birds" }), { target: { value: "250" } });
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

    fireEvent.click(within(getRowByCellText("Hen House 1")).getByRole("button", { name: "birds" }));

    const cullRow = await screen.findByRole("row", { name: /Cull/ });
    expect(mockListMovements).toHaveBeenCalledWith("f1", { limit: 50, offset: 0 });
    expect(screen.getByRole("heading", { name: /Bird ledger — Hen House 1/ })).toBeInTheDocument();
    expect(within(cullRow).getByText("03/15/2026")).toBeInTheDocument();
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

    fireEvent.click(within(getRowByCellText("Hen House 1")).getByRole("button", { name: "birds" }));

    // The movement form lives in its own dialog, opened from the ledger panel.
    fireEvent.click(await screen.findByRole("button", { name: "Record movement" }));
    fireEvent.change(within(dialog()).getByLabelText("Date"), { target: { value: "2026-05-01" } });
    fireEvent.change(within(dialog()).getByLabelText("Type"), { target: { value: "Adjustment" } }); // off "Cull" default
    fireEvent.change(within(dialog()).getByRole("spinbutton", { name: "Birds" }), { target: { value: "-5" } });
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
    fireEvent.click(within(getRowByCellText("Hen House 1")).getByRole("button", { name: "edit" }));

    fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));

    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(mockUpdate).not.toHaveBeenCalled();
  });

  it("closes the movement dialog on Cancel without writing", async () => {
    mockListMovements.mockResolvedValue([]);
    await renderReady(ADMIN, [ACTIVE]);
    fireEvent.click(within(getRowByCellText("Hen House 1")).getByRole("button", { name: "birds" }));
    fireEvent.click(await screen.findByRole("button", { name: "Record movement" }));

    fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));

    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(mockRecordMovement).not.toHaveBeenCalled();
  });
});

// #479 — one slot per PLACE a message can appear. FlocksPage has three
// dialogs (create, edit, record-movement) plus a background read outside any
// of them (the bird ledger), so it exercises the routing on both sides.
describe("FlocksPage error placement (#479)", () => {
  it("shows a failed create inside the dialog, not on the page behind it", async () => {
    mockCreate.mockRejectedValue(new ApiError(422, "Validation failed", "Name already used."));
    await renderReady(ADMIN, [ACTIVE]);
    openCreate();
    fireEvent.change(within(dialog()).getByLabelText("Name *"), { target: { value: "Rhode Reds" } });
    fireEvent.change(within(dialog()).getByLabelText("Breed *"), { target: { value: "Rhode Island Red" } });
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Add flock" }));
    });

    expect(within(dialog()).getByText("Name already used.")).toBeInTheDocument();
    // Exactly one copy: the page must not render the dialog's message too.
    expect(screen.getAllByText("Name already used.")).toHaveLength(1);
  });

  it("shows a failed edit inside the dialog, not on the page behind it", async () => {
    mockUpdate.mockRejectedValue(new ApiError(409, "Conflict", "Someone else changed this flock."));
    await renderReady(ADMIN, [ACTIVE]);
    fireEvent.click(within(getRowByCellText("Hen House 1")).getByRole("button", { name: "edit" }));
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Save" }));
    });

    expect(within(dialog()).getByText("Someone else changed this flock.")).toBeInTheDocument();
    expect(screen.getAllByText("Someone else changed this flock.")).toHaveLength(1);
  });

  it("shows a failed movement record inside the dialog, not on the page behind it", async () => {
    mockListMovements.mockResolvedValue([]);
    mockRecordMovement.mockRejectedValue(new ApiError(422, "Validation failed", "Quantity exceeds current birds."));
    await renderReady(ADMIN, [ACTIVE]);
    fireEvent.click(within(getRowByCellText("Hen House 1")).getByRole("button", { name: "birds" }));
    fireEvent.click(await screen.findByRole("button", { name: "Record movement" }));
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Record" }));
    });

    expect(within(dialog()).getByText("Quantity exceeds current birds.")).toBeInTheDocument();
    expect(screen.getAllByText("Quantity exceeds current birds.")).toHaveLength(1);
  });

  // Displacement: the edit scope is fixed ("edit"), and a second flock's edit
  // can begin without the first being dismissed — the row buttons behind the
  // backdrop are reachable to a screen reader's virtual cursor (#480). Without
  // an abandon on the switch, flock A's failed save renders inside flock B's
  // dialog (pi review of #491).
  it("does not carry one flock's failed edit into another flock's dialog", async () => {
    mockUpdate.mockRejectedValue(new ApiError(409, "Conflict", "Someone else changed this flock."));
    await renderReady(ADMIN, [ACTIVE, DEPLETED]);
    fireEvent.click(within(getRowByCellText("Hen House 1")).getByRole("button", { name: "edit" }));
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Save" }));
    });
    expect(within(dialog()).getByText("Someone else changed this flock.")).toBeInTheDocument();

    fireEvent.click(within(screen.getByRole("row", { name: /Depleted Flock/ })).getByRole("button", { name: "edit" }));
    // The dialog really swapped records — otherwise the absence below could
    // pass for the wrong reason.
    expect(within(dialog()).getByLabelText("Edit name")).toHaveValue("Depleted Flock");
    expect(screen.queryByText("Someone else changed this flock.")).not.toBeInTheDocument();
  });

  it("keeps a bird-ledger read failure out of an open create dialog", async () => {
    // The ledger toggle stays reachable in the DOM behind a portalled dialog
    // (jsdom does not enforce the backdrop's visual occlusion), so this is a
    // real background failure landing while a dialog happens to be open —
    // not a contrived one.
    mockListMovements.mockRejectedValue(new ApiError(500, "Server error", "boom"));
    await renderReady(ADMIN, [ACTIVE]);
    openCreate();

    await act(async () => {
      fireEvent.click(within(getRowByCellText("Hen House 1")).getByRole("button", { name: "birds" }));
    });

    const message = i18n.t("flocks:loadMovementsFailed");
    expect(within(dialog()).queryByText(message)).not.toBeInTheDocument();
    expect(screen.getByText(message)).toBeInTheDocument();
  });

  it("keeps a bird-ledger failure on the page while the create dialog opens and its own write fails", async () => {
    // Two live messages at once, in their own places. The page's belongs to
    // the ledger read the user has not dealt with; the dialog's to the form
    // in front of them. Neither may erase the other.
    mockListMovements.mockRejectedValue(new ApiError(500, "Server error", "boom"));
    mockCreate.mockRejectedValue(new ApiError(422, "Validation failed", "Name already used."));
    await renderReady(ADMIN, [ACTIVE]);

    await act(async () => {
      fireEvent.click(within(getRowByCellText("Hen House 1")).getByRole("button", { name: "birds" }));
    });
    const pageMessage = i18n.t("flocks:loadMovementsFailed");
    await screen.findByText(pageMessage);

    openCreate();
    expect(screen.getByText(pageMessage)).toBeInTheDocument();

    fireEvent.change(within(dialog()).getByLabelText("Name *"), { target: { value: "Rhode Reds" } });
    fireEvent.change(within(dialog()).getByLabelText("Breed *"), { target: { value: "Rhode Island Red" } });
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Add flock" }));
    });

    expect(within(dialog()).getByText("Name already used.")).toBeInTheDocument();
    expect(screen.getByText(pageMessage)).toBeInTheDocument();
  });
});

describe("FlocksPage lifecycle", () => {
  it("depletes an active flock after confirmation", async () => {
    mockDeplete.mockResolvedValue(undefined);
    await renderReady(ADMIN, [ACTIVE]);

    await act(async () => {
      fireEvent.click(within(getRowByCellText("Hen House 1")).getByRole("button", { name: "deplete" }));
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
      fireEvent.click(within(getRowByCellText("Hen House 1")).getByRole("button", { name: "deplete" }));
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

describe("FlocksPage pending states (#236)", () => {
  it("hands off from the confirm dialog: the row's archive button is the pending indicator, siblings disable without spinning, and settle re-enables it", async () => {
    const gate = deferred<void>();
    mockArchive.mockReturnValue(gate.promise);
    await renderReady(ADMIN, [ACTIVE, DEPLETED]);

    await act(async () => {
      fireEvent.click(within(getRowByCellText("Hen House 1")).getByRole("button", { name: "archive" }));
    });
    // Dialog buttons carry no busy state — the dialog settles before any I/O.
    await answer("Archive flock");

    // Once the confirmed request is in flight the dialog is gone and the
    // ORIGINATING row control is the pending indicator.
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    const row = screen.getByRole("row", { name: /Hen House 1/ });
    const archiveButton = within(row).getByRole("button", { name: "archive" });
    expect(archiveButton).toBeDisabled();
    expect(archiveButton).toHaveAttribute("aria-busy", "true");

    // Sibling row: whole screen inert, but nothing else spins.
    const sibling = screen.getByRole("row", { name: /Depleted Flock/ });
    for (const name of ["archive", "reactivate", "edit"]) {
      const button = within(sibling).getByRole("button", { name });
      expect(button).toBeDisabled();
      expect(button).not.toHaveAttribute("aria-busy");
    }

    await act(async () => {
      gate.resolve();
    });

    // After the long confirmed request settles, the originating button is
    // present and re-enabled; where focus sits during the disabled window is
    // explicitly unasserted (design appendix — accepted limitation).
    await waitFor(() => {
      expect(within(getRowByCellText("Hen House 1"))
        .getByRole("button", { name: "archive" })).toBeEnabled();
    });
    expect(document.querySelector('[aria-busy="true"]')).toBeNull();
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
  it("hides flock creation and every lifecycle action from a worker, but leaves the ledger read open", async () => {
    await renderReady(WORKER, [ACTIVE, DEPLETED]);

    // Creating a flock is Owner/Manager administration (#388) — a scoped
    // Worker cannot assign the flock it just created.
    expect(screen.queryByRole("button", { name: "New flock" })).not.toBeInTheDocument();

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

    fireEvent.click(within(getRowByCellText("Hen House 1")).getByRole("button", { name: "birds" }));

    await screen.findByRole("row", { name: /Cull/ }); // rows render read-only
    // No way in: the action that opens the movement dialog is admin-only.
    expect(screen.queryByRole("button", { name: "Record movement" })).not.toBeInTheDocument();
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });

  // Same controlled-AuthContext technique UsersPage.test.tsx uses for its own
  // mid-session demotion cases: a live rerender (not a fresh mount) proves the
  // dialog's `open={creating && isAdmin}` gate reacts to a ROLE change, not
  // just to isAdmin at mount time.
  it("closes an open create dialog the instant the role demotes away from admin", async () => {
    const tree = (isAdmin: boolean) => (
      <MemoryRouter initialEntries={["/"]}>
        <AuthContext.Provider value={{
          isAuthenticated: true, isLoading: false, isAdmin, role: (isAdmin ? "Admin" : "Worker") as Role,
          userId: "u1", mustChangePassword: false, unauthenticatedReason: null,
          login: vi.fn(), logout: vi.fn(),
        }}
        >
          <FlocksPage />
        </AuthContext.Provider>
      </MemoryRouter>
    );
    mockListFlocks.mockResolvedValue([ACTIVE]);
    const view = render(tree(true));
    await screen.findByRole("row", { name: /Hen House 1/ });

    fireEvent.click(screen.getByRole("button", { name: "New flock" }));
    expect(screen.getByRole("dialog")).toBeInTheDocument();

    view.rerender(tree(false)); // demoted mid-session

    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });
});

// #494 — the record-history column is a shared component, well tested on its
// own; what is NOT tested by that unit suite is the per-page WIRING that hands
// it the CORRECT row's history object. A page passing the wrong variable (a
// different row, or a stray constant) would go uncaught otherwise.
describe("FlocksPage record history column (#494)", () => {
  it("shows the record history column for the row that has one", async () => {
    const HISTORY_FLOCK: Flock = {
      ...RECORD_HISTORY,
      id: "f9", farmId: "farm1", houseId: "h1", name: "Provenance Coop", breed: "Orpington",
      placementDate: "2026-02-01", initialCount: 50, currentBirds: 50, status: "Active",
    };
    await renderReady(ADMIN, [ACTIVE, HISTORY_FLOCK]);

    const historyRow = screen.getByRole("row", { name: /Provenance Coop/ });
    expect(within(historyRow).getByText(/ana@farm\.test/)).toBeInTheDocument();
    expect(within(historyRow).getByText(/bo@farm\.test/)).toBeInTheDocument();

    // The OTHER row must not carry the history row's data — this is what
    // catches every row being wired to the same object.
    const otherRow = screen.getByRole("row", { name: /Hen House 1/ });
    expect(within(otherRow).queryByText(/ana@farm\.test/)).not.toBeInTheDocument();
  });
});

// #493 — full audit trail, distinct from the two-point summary above.
describe("FlocksPage audit history link (#493)", () => {
  it("links each row to its own entity-scoped audit history", async () => {
    await renderReady(ADMIN, [ACTIVE, DEPLETED]);

    const activeRow = screen.getByRole("row", { name: /Hen House 1/ });
    expect(within(activeRow).getByRole("link", { name: "Audit history" }))
      .toHaveAttribute("href", "/audit?entityId=f1");

    const depletedRow = screen.getByRole("row", { name: /Depleted Flock/ });
    expect(within(depletedRow).getByRole("link", { name: "Audit history" }))
      .toHaveAttribute("href", "/audit?entityId=f2");
  });

  // codex review of #516 — a worker can view Flocks but /api/v1/audit is
  // AdminOnly; an ungated link would only lead to a 403.
  it("hides the link from a non-admin", async () => {
    await renderReady(WORKER, [ACTIVE]);
    // Proves the row rendered — otherwise "the link is absent" is true for
    // the wrong reason (review round 1 finding: a vacuous pass looks
    // identical to a real gate).
    const row = screen.getByRole("row", { name: /Hen House 1/ });
    expect(within(row).queryByRole("link", { name: "Audit history" })).not.toBeInTheDocument();
  });
});

// ---------------------------------------------------------------------------
// i18n wiring (#182, Task 19, batch B3 — the last B3 screen)
// ---------------------------------------------------------------------------

// `flocks` is English-only (not in TRANSLATED_NAMESPACES — see
// translations-status.ts), so under ANY UI language the rendered text falls
// back to this exact English string, same as a still-hardcoded literal would
// render — asserting it, even under a non-English locale, would prove nothing
// (CONTRIBUTING-i18n.md's fallback trap). Swap the catalog value at runtime
// instead, the same i18n.addResource technique the other batches use, so each
// marker only renders if the screen actually reads the catalog rather than a
// literal that happens to still match it.
describe("FlocksPage i18n wiring (#182, Task 19)", () => {
  function withOverride(ns: string, key: string, value: string, run: () => Promise<void> | void) {
    const original = i18n.getResource("en", ns, key) as string;
    i18n.addResource("en", ns, key, value);
    return Promise.resolve(run()).finally(() => {
      i18n.addResource("en", ns, key, original);
    });
  }

  it("reads the heading from the catalog, not a hardcoded literal", async () => {
    await withOverride("flocks", "title", "TITLE-MARKER", async () => {
      await renderReady(ADMIN, [ACTIVE]);
      expect(screen.getByRole("heading", { name: "TITLE-MARKER" })).toBeInTheDocument();
      expect(screen.queryByRole("heading", { name: "Flocks" })).not.toBeInTheDocument();
    });
  });

  // #493 — the audit-history link's label lives in `common` (shared across all
  // six pages that carry it, per ProvenanceCell's own namespace convention),
  // tested here where it was first introduced rather than repeated on every
  // page in Slice 3.
  it("reads the audit-history link label from the common catalog, not a hardcoded literal", async () => {
    await withOverride("common", "recordHistory.viewHistoryLink", "AUDIT-HISTORY-MARKER", async () => {
      await renderReady(ADMIN, [ACTIVE]);
      expect(screen.getByRole("link", { name: "AUDIT-HISTORY-MARKER" })).toBeInTheDocument();
      expect(screen.queryByRole("link", { name: "Audit history" })).not.toBeInTheDocument();
    });
  });

  it("reads the new-flock button label from the catalog, not a hardcoded literal", async () => {
    // Not renderReady(): its own readiness probe waits on this exact button
    // text, which the override below replaces — wait on flock data instead.
    await withOverride("flocks", "newFlockButton", "NEW-FLOCK-MARKER", async () => {
      mockListFlocks.mockResolvedValue([ACTIVE]);
      renderWithProviders(<FlocksPage />, { token: ADMIN });
      expect(await screen.findByRole("button", { name: "NEW-FLOCK-MARKER" })).toBeInTheDocument();
      expect(screen.queryByRole("button", { name: "New flock" })).not.toBeInTheDocument();
    });
  });

  // Proves the deplete-confirm dialog's title reads the catalog template AND
  // interpolates the flock's free-form NAME (DATA) — a hardcoded literal
  // would fail to pick up the marker text even though the name would still
  // look right.
  it("interpolates the flock name into the deplete-confirm title from the catalog", async () => {
    await withOverride("flocks", "depleteConfirmTitle", "DEPLETE-MARKER {{name}} MARKER-END", async () => {
      await renderReady(ADMIN, [ACTIVE]);
      await act(async () => {
        fireEvent.click(within(getRowByCellText("Hen House 1")).getByRole("button", { name: "deplete" }));
      });
      expect(screen.getByRole("dialog")).toHaveAccessibleName("DEPLETE-MARKER Hen House 1 MARKER-END");
    });
  });

  // Proves the ledger's Type picker AND the ledger's Type cell both read the
  // flock-movement ENUM label from the catalog (via flockMovementLabel), not
  // the raw wire value or a hardcoded literal — MOVEMENTS' first row's type
  // is "Cull".
  it("reads the flock-movement enum label from the catalog for the picker and the ledger cell", async () => {
    await withOverride("enums", "flockMovement.Cull", "CULL-MARKER", async () => {
      mockListMovements.mockResolvedValue(MOVEMENTS);
      await renderReady(ADMIN, [ACTIVE]);
      fireEvent.click(within(getRowByCellText("Hen House 1")).getByRole("button", { name: "birds" }));
      const cullRow = await screen.findByRole("row", { name: /CULL-MARKER/ });
      expect(within(cullRow).getByText("CULL-MARKER")).toBeInTheDocument();
      expect(screen.queryByRole("row", { name: /^Cull\b/ })).not.toBeInTheDocument();

      fireEvent.click(screen.getByRole("button", { name: "Record movement" }));
      expect(within(screen.getByRole("dialog")).getByRole("option", { name: "CULL-MARKER" })).toBeInTheDocument();
      expect(within(screen.getByRole("dialog")).queryByRole("option", { name: "Cull" })).not.toBeInTheDocument();
    });
  });

  // Proves the flocks table's Status badge reads the status ENUM label from
  // the catalog (via statusLabel), not a hardcoded literal — ACTIVE's status
  // is "Active".
  it("reads the status enum label from the catalog for the flocks-table badge", async () => {
    await withOverride("enums", "status.Active", "ACTIVE-MARKER", async () => {
      await renderReady(ADMIN, [ACTIVE]);
      const row = screen.getByRole("row", { name: /Hen House 1/ });
      expect(within(row).getByText("ACTIVE-MARKER")).toBeInTheDocument();
      expect(within(row).queryByText("Active")).not.toBeInTheDocument();
    });
  });

  // Proves the show-archived toggle's label reads the catalog template AND
  // interpolates the client-side archived-flock count — plain numeric DATA.
  it("interpolates the archived count into the show-archived label from the catalog", async () => {
    await withOverride("flocks", "showArchivedLabel", "SHOW-MARKER {{count}} MARKER-END", async () => {
      await renderReady(ADMIN, [ACTIVE, ARCHIVED]);
      expect(screen.getByText("SHOW-MARKER 1 MARKER-END")).toBeInTheDocument();
      expect(screen.queryByText(/show 1 archived/)).not.toBeInTheDocument();
    });
  });
});

// #511 — the bird ledger asked for 50 rows and rendered them with no pager, so
// the oldest movements were unreachable. These pin the paged behaviour and the
// per-flock identity that keeps one flock's rows off another flock's heading.
const movementRow = (over: Partial<BirdMovement> = {}): BirdMovement => ({
  id: "m0", flockId: "f1", date: "2026-08-01", type: "Cull", quantity: 1, note: "note", ...over,
});
const movementPage = (n: number, prefix = "m") =>
  Array.from({ length: n }, (_, i) =>
    movementRow({ id: `${prefix}${i}`, note: `${prefix} note ${String(i).padStart(3, "0")}` }));

// Same inline shape every existing ledger test in this file uses.
const openLedgerFor = (rowName: RegExp) =>
  fireEvent.click(
    within(screen.getByRole("row", { name: rowName })).getByRole("button", { name: "birds" }));

describe("FlocksPage bird ledger paging (#511)", () => {
  it("reaches a movement past the first page through load more", async () => {
    mockListMovements.mockResolvedValueOnce(movementPage(50));
    await renderReady(ADMIN, [ACTIVE]);
    await act(async () => { openLedgerFor(/Hen House 1/); });
    await screen.findByText("m note 000");
    expect(mockListMovements).toHaveBeenCalledWith("f1",
      expect.objectContaining({ limit: 50, offset: 0 }));

    mockListMovements.mockResolvedValueOnce([movementRow({ id: "old", note: "oldest row" })]);
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "load more" }));
    });

    expect(mockListMovements).toHaveBeenLastCalledWith("f1",
      expect.objectContaining({ offset: 50 }));
    expect(await screen.findByText("oldest row")).toBeInTheDocument();
    expect(screen.getByText("m note 000")).toBeInTheDocument();
  });

  it("withdraws the pager on a short page", async () => {
    mockListMovements.mockResolvedValueOnce(movementPage(3));
    await renderReady(ADMIN, [ACTIVE]);
    await act(async () => { openLedgerFor(/Hen House 1/); });
    await screen.findByText("m note 000");
    expect(screen.queryByRole("button", { name: "load more" })).not.toBeInTheDocument();
  });

  it("hides the previous flock's movements while the next flock's ledger is loading", async () => {
    // INV-4, render half. Flock one's page has ALREADY LANDED, so `rows` holds
    // it; the user then switches straight to flock two and that replacement is
    // in flight. The rows still in `rows` belong to a flock the user has left,
    // and `reloading` is the only state that knows it. Do NOT close flock one
    // first: closing swaps in the empty no-flock page and `rows` would be []
    // before the assertion, which would make this test pass no matter what the
    // render guard does.
    mockListMovements.mockResolvedValueOnce([movementRow({ id: "a1", note: "flock one row" })]);
    await renderReady(ADMIN, [ACTIVE, DEPLETED]);
    await act(async () => { openLedgerFor(/Hen House 1/); });
    await screen.findByText("flock one row");

    let releaseSecond!: (rows: BirdMovement[]) => void;
    mockListMovements.mockReturnValueOnce(new Promise((r) => { releaseSecond = r; }));
    await act(async () => { openLedgerFor(/Depleted Flock/); });

    expect(screen.queryByText("flock one row")).not.toBeInTheDocument();

    await act(async () => {
      releaseSecond([movementRow({ id: "b1", flockId: "f2", note: "flock two row" })]);
    });
    expect(await screen.findByText("flock two row")).toBeInTheDocument();
  });

  it("refreshes every loaded page after recording a movement", async () => {
    mockListMovements.mockResolvedValueOnce(movementPage(50));
    await renderReady(ADMIN, [ACTIVE]);
    await act(async () => { openLedgerFor(/Hen House 1/); });
    await screen.findByText("m note 000");

    mockListMovements.mockResolvedValueOnce([movementRow({ id: "old", note: "oldest row" })]);
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "load more" }));
    });
    await screen.findByText("oldest row");

    // The refresh has to be OBSERVABLE. If both sides of the write return the
    // same fixture, a page that never refreshes renders exactly the DOM a page
    // that refreshed does, and the assertion cannot fail. So the re-read
    // returns DIFFERENT text for both pages.
    mockRecordMovement.mockResolvedValue({ id: "mv9" });
    mockListMovements.mockResolvedValueOnce(
      movementPage(50).map((m, i) => ({ ...m, note: `refreshed ${String(i).padStart(3, "0")}` })));
    mockListMovements.mockResolvedValueOnce([movementRow({ id: "old", note: "refreshed oldest" })]);

    // Copied from the existing "records a bird movement with the type/quantity/
    // date body and a key" test in this file — same dialog, same controls.
    fireEvent.click(await screen.findByRole("button", { name: "Record movement" }));
    fireEvent.change(within(dialog()).getByLabelText("Date"), { target: { value: "2026-05-01" } });
    fireEvent.change(within(dialog()).getByLabelText("Type"), { target: { value: "Adjustment" } });
    fireEvent.change(within(dialog()).getByRole("spinbutton", { name: "Birds" }), { target: { value: "-5" } });
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Record" }));
    });

    // Both pages the user had loaded were re-read, not just page one.
    expect(await screen.findByText("refreshed oldest")).toBeInTheDocument();
    expect(screen.getByText("refreshed 000")).toBeInTheDocument();
  });

  // One test, two locales, per the round-2 runbook. Two adaptations from a
  // literal read of the CustomersPage precedent, both reported: (1)
  // renderReady() (via renderWithProviders/render) does not auto-unmount
  // between calls — that only happens via afterEach BETWEEN tests — so an
  // explicit cleanup() between the two locale checks is required or the
  // second render's row query collides with the first's still-mounted tree;
  // (2) openLedgerFor()'s hardcoded "birds" button name is English-only —
  // flocks:openLedgerButton is itself translated ("aves"/"mga ibon"), so
  // opening the ledger under es/tl needs the CURRENT locale's label, read
  // via i18n.t rather than the helper.
  it("renders the pager label from the active locale", async () => {
    mockListMovements.mockResolvedValueOnce(movementPage(50));
    await i18n.changeLanguage("es");
    try {
      await renderReady(ADMIN, [ACTIVE]);
      const openLabel = i18n.t("flocks:openLedgerButton");
      await act(async () => {
        fireEvent.click(within(getRowByCellText("Hen House 1")).getByRole("button", { name: openLabel }));
      });
      await screen.findByText("m note 000");
      expect(screen.getByRole("button", { name: "cargar más" })).toBeInTheDocument();
    } finally {
      await i18n.changeLanguage("en");
      cleanup();
    }

    mockListMovements.mockResolvedValueOnce(movementPage(50));
    await i18n.changeLanguage("tl");
    try {
      await renderReady(ADMIN, [ACTIVE]);
      const openLabel = i18n.t("flocks:openLedgerButton");
      await act(async () => {
        fireEvent.click(within(getRowByCellText("Hen House 1")).getByRole("button", { name: openLabel }));
      });
      await screen.findByText("m note 000");
      expect(screen.getByRole("button", { name: "mag-load pa" })).toBeInTheDocument();
    } finally {
      await i18n.changeLanguage("en");
    }
  });

  it("keeps the loaded movements on screen when a load-more fails, and offers the retry", async () => {
    // AC3. usePagedList keeps `rows` and `hasMore` when an EXTENSION fails —
    // only a failed REPLACEMENT empties them — so the screen must not throw
    // the table away on `error`. Paging deep and hitting one transient failure
    // must not cost the user everything already on screen.
    mockListMovements.mockResolvedValueOnce(movementPage(50));
    await renderReady(ADMIN, [ACTIVE]);
    await act(async () => { openLedgerFor(/Hen House 1/); });
    await screen.findByText("m note 000");

    mockListMovements.mockRejectedValueOnce(new ApiError(500, "Server error", "ledger down"));
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "load more" }));
    });

    // The error is shown...
    // The runbook literal used inventory's ledger-error string; FlocksPage's
    // own key is flocks:loadMovementsFailed, whose English text is different
    // — corrected and reported.
    expect(screen.getByText("Could not load movements.")).toBeInTheDocument();
    // ...and the rows already loaded are STILL THERE, with the pager to retry.
    expect(screen.getByText("m note 000")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "load more" })).toBeInTheDocument();
  });
});
