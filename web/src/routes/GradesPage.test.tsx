import { describe, it, expect, vi, beforeEach } from "vitest";
import { screen, within, fireEvent, act } from "@testing-library/react";
import { GradesPage } from "./GradesPage";
import { renderWithProviders } from "../test/renderWithProviders";
import {
  activateEggGrade, createEggGrade, deactivateEggGrade, listEggGrades, updateEggGrade,
} from "../api/cluckwork";
import type { EggGrade } from "../api/cluckwork";
import { ApiError } from "../api/client";
import i18n from "../i18n";

// Network seam only; ApiError stays real (errorMessage branches on it).
vi.mock("../api/cluckwork", () => ({
  listEggGrades: vi.fn(),
  createEggGrade: vi.fn(),
  updateEggGrade: vi.fn(),
  deactivateEggGrade: vi.fn(),
  activateEggGrade: vi.fn(),
}));

const mockList = vi.mocked(listEggGrades);
const mockCreate = vi.mocked(createEggGrade);
const mockUpdate = vi.mocked(updateEggGrade);
const mockDeactivate = vi.mocked(deactivateEggGrade);
const mockActivate = vi.mocked(activateEggGrade);

const GRADE_A: EggGrade = { id: "g1", farmId: "f", name: "Grade A", gradeType: "Size", sortOrder: 1, isSaleable: true, dailyEntryKind: "Manual", active: true };
const GRADE_OLD: EggGrade = { id: "g2", farmId: "f", name: "Legacy", gradeType: "Quality", sortOrder: 2, isSaleable: false, dailyEntryKind: "Manual", active: false };

const ADMIN = { sub: "u1", role: "Admin" };
const WORKER = { sub: "u1" };

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  mockList.mockResolvedValue([GRADE_A, GRADE_OLD]);
});

async function renderReady(token: Record<string, unknown>) {
  renderWithProviders(<GradesPage />, { token });
  await screen.findByText("Grade A");
}

// F131: add/edit moved out of the page into a dialog. The behavioural
// assertions below are unchanged — they just open the dialog first.
const openCreate = () => fireEvent.click(screen.getByRole("button", { name: "New grade" }));
const dialog = () => screen.getByRole("dialog");

// A promise the test resolves by hand — holds a request open so the busy
// window is asserted deterministically, no timing guesses (client.test.ts idiom).
function deferred<T>() {
  let resolve!: (v: T) => void;
  const promise = new Promise<T>((r) => (resolve = r));
  return { promise, resolve };
}

describe("GradesPage display", () => {
  it("renders each grade with saleable + status columns", async () => {
    await renderReady(ADMIN);
    const rowA = screen.getByRole("row", { name: /Grade A/ });
    expect(within(rowA).getByText("yes")).toBeInTheDocument(); // saleable
    expect(within(rowA).getByText("Active")).toBeInTheDocument();
    const rowOld = screen.getByRole("row", { name: /Legacy/ });
    expect(within(rowOld).getByText("—")).toBeInTheDocument(); // not saleable
    expect(within(rowOld).getByText("Inactive")).toBeInTheDocument();
  });
});

describe("GradesPage admin actions", () => {
  it("creates a grade with the form values, then closes and clears the form", async () => {
    mockCreate.mockResolvedValue({ id: "g3" });
    await renderReady(ADMIN);
    openCreate();

    fireEvent.change(within(dialog()).getByLabelText("Name *"), { target: { value: "Jumbo" } });
    fireEvent.change(within(dialog()).getByLabelText("Type"), { target: { value: "Quality" } }); // off the "Size" default
    fireEvent.change(within(dialog()).getByLabelText("Sort"), { target: { value: "3" } });
    fireEvent.click(within(dialog()).getByLabelText("saleable")); // default true → false
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Add grade" }));
    });

    expect(mockCreate.mock.calls[0][0]).toEqual({
      name: "Jumbo", gradeType: "Quality", sortOrder: 3, isSaleable: false,
    });
    expect(mockCreate.mock.calls[0][1]).toEqual(expect.any(String)); // idempotency key
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument(); // success dismisses it
    openCreate();
    expect(within(dialog()).getByLabelText("Name *")).toHaveValue(""); // reset on success
  });

  it("saves an edit with the edited name/sort/saleable", async () => {
    mockUpdate.mockResolvedValue(undefined);
    await renderReady(ADMIN);

    const rowA = screen.getByRole("row", { name: /Grade A/ });
    fireEvent.click(within(rowA).getByRole("button", { name: "edit" }));
    // the dialog is seeded from the row, then all three fields move off it.
    // The edit field is "Name", not "Name *": the row's save was a plain button,
    // so it never carried a required marker — the dialog keeps that parity.
    expect(within(dialog()).getByLabelText("Name")).toHaveValue("Grade A");
    fireEvent.change(within(dialog()).getByLabelText("Name"), { target: { value: "Large" } });
    fireEvent.change(within(dialog()).getByLabelText("Sort"), { target: { value: "5" } });
    fireEvent.click(within(dialog()).getByLabelText("saleable")); // saleable true → false
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Save" }));
    });

    expect(mockUpdate.mock.calls[0][0]).toBe("g1");
    expect(mockUpdate.mock.calls[0][1]).toEqual({ name: "Large", sortOrder: 5, isSaleable: false });
    expect(mockUpdate.mock.calls[0][2]).toEqual(expect.any(String)); // idempotency key
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });

  it("deactivates an active grade and activates an inactive one", async () => {
    mockDeactivate.mockResolvedValue(undefined);
    mockActivate.mockResolvedValue(undefined);
    await renderReady(ADMIN);

    await act(async () => {
      fireEvent.click(within(screen.getByRole("row", { name: /Grade A/ })).getByRole("button", { name: "deactivate" }));
    });
    expect(mockDeactivate).toHaveBeenCalledWith("g1", expect.any(String));

    await act(async () => {
      fireEvent.click(within(screen.getByRole("row", { name: /Legacy/ })).getByRole("button", { name: "activate" }));
    });
    expect(mockActivate).toHaveBeenCalledWith("g2", expect.any(String));
  });

  it("replays the SAME idempotency key after a failed create, and rotates it after success", async () => {
    // fail once, then succeed twice
    mockCreate.mockRejectedValueOnce(new ApiError(500, "Server error", "boom"));
    mockCreate.mockResolvedValue({ id: "g3" });
    await renderReady(ADMIN);
    openCreate();
    const name = () => within(dialog()).getByLabelText("Name *");
    const submit = async () => {
      await act(async () => {
        fireEvent.click(within(dialog()).getByRole("button", { name: "Add grade" }));
      });
    };

    fireEvent.change(name(), { target: { value: "One" } });
    await submit();
    // A failed save keeps the dialog up, with the error inside it.
    expect(within(dialog()).getByText(/Server error|boom/)).toBeInTheDocument();

    fireEvent.change(name(), { target: { value: "One" } });
    await submit();

    openCreate(); // success closed it
    fireEvent.change(name(), { target: { value: "Two" } });
    await submit();

    const k1 = mockCreate.mock.calls[0][1];
    const k2 = mockCreate.mock.calls[1][1];
    const k3 = mockCreate.mock.calls[2][1];
    expect(k2).toBe(k1); // failure kept the key → exact replay
    expect(k3).not.toBe(k2); // success rotated it → the next write is fresh
  });
});

describe("GradesPage pending states (#236)", () => {
  it("maps a skipped run to failure: a submit landing under an open flight writes nothing and keeps its dialog and values", async () => {
    const gate = deferred<void>();
    mockDeactivate.mockReturnValue(gate.promise);
    await renderReady(ADMIN);

    // A long-running row action opens the flight and spins on its own scope.
    await act(async () => {
      fireEvent.click(within(screen.getByRole("row", { name: /Grade A/ })).getByRole("button", { name: "deactivate" }));
    });
    expect(within(screen.getByRole("row", { name: /Grade A/ })).getByRole("button", { name: "deactivate" }))
      .toHaveAttribute("aria-busy", "true");

    // The create form fires UNDER that flight (its button is disabled, but
    // Enter in a field still submits the form — the double-fire the boolean
    // wrapper must map to false, never to success).
    openCreate();
    fireEvent.change(within(dialog()).getByLabelText("Name *"), { target: { value: "Jumbo" } });
    await act(async () => {
      fireEvent.submit(within(dialog()).getByLabelText("Name *").closest("form")!);
    });

    expect(mockCreate).not.toHaveBeenCalled(); // skipped, not queued
    expect(screen.getByRole("dialog")).toBeInTheDocument(); // a skip must not close the dialog…
    expect(within(dialog()).getByLabelText("Name *")).toHaveValue("Jumbo"); // …or reset the form

    await act(async () => {
      gate.resolve();
    });
    expect(mockDeactivate).toHaveBeenCalledTimes(1);
    // The settled flight left the dialog untouched and ready for a real submit.
    expect(within(dialog()).getByLabelText("Name *")).toHaveValue("Jumbo");
    expect(within(dialog()).getByRole("button", { name: "Add grade" })).toBeEnabled();
  });
});

describe("GradesPage dialog dismissal", () => {
  it("closes the create dialog on Cancel without writing", async () => {
    await renderReady(ADMIN);
    openCreate();
    fireEvent.change(within(dialog()).getByLabelText("Name *"), { target: { value: "Abandoned" } });

    fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));

    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(mockCreate).not.toHaveBeenCalled();
  });

  it("closes the edit dialog on Cancel without writing", async () => {
    await renderReady(ADMIN);
    fireEvent.click(within(screen.getByRole("row", { name: /Grade A/ })).getByRole("button", { name: "edit" }));

    fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));

    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(mockUpdate).not.toHaveBeenCalled();
  });
});

// #479 — one slot per PLACE a message can appear. Create and edit each get
// their own dialog slot; the initial load and the row-level activate/
// deactivate writes (neither behind a dialog) share the page's.
describe("GradesPage error placement (#479)", () => {
  it("shows a failed create inside the dialog, not on the page behind it", async () => {
    mockCreate.mockRejectedValue(new ApiError(422, "Validation failed", "Name already exists."));
    await renderReady(ADMIN);
    openCreate();
    fireEvent.change(within(dialog()).getByLabelText("Name *"), { target: { value: "Dup" } });

    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Add grade" }));
    });

    expect(within(dialog()).getByText("Name already exists.")).toBeInTheDocument();
    // Exactly one copy: the page must not render the dialog's message too.
    expect(screen.getAllByText("Name already exists.")).toHaveLength(1);
  });

  it("shows a failed edit inside the dialog, not on the page behind it", async () => {
    mockUpdate.mockRejectedValue(new ApiError(422, "Validation failed", "Name already exists."));
    await renderReady(ADMIN);
    fireEvent.click(within(screen.getByRole("row", { name: /Grade A/ })).getByRole("button", { name: "edit" }));

    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Save" }));
    });

    expect(within(dialog()).getByText("Name already exists.")).toBeInTheDocument();
    expect(screen.getAllByText("Name already exists.")).toHaveLength(1);
  });

  // GradesPage's only background READ is the initial list load, which blocks
  // the create trigger from rendering at all when it fails — so there is no
  // read that can race an open dialog here. The row-level deactivate/activate
  // writes are the screen's other PAGE-scoped failure source (not behind a
  // dialog), and they reach the same slot the same way; this proves one stays
  // out of a dialog that is open when it lands.
  it("keeps a row-action failure out of an open create dialog", async () => {
    mockDeactivate.mockRejectedValue(new ApiError(500, "Server error", "boom"));
    await renderReady(ADMIN);
    openCreate();
    fireEvent.change(within(dialog()).getByLabelText("Name *"), { target: { value: "Jumbo" } });

    await act(async () => {
      fireEvent.click(within(screen.getByRole("row", { name: /Grade A/ })).getByRole("button", { name: "deactivate" }));
    });

    expect(within(dialog()).queryByText(/Server error|boom/)).not.toBeInTheDocument();
    expect(screen.getByText(/Server error|boom/)).toBeInTheDocument();
  });

  it("keeps a page failure while the dialog opens and its own write fails", async () => {
    mockDeactivate.mockRejectedValue(new ApiError(500, "Server error", "boom"));
    mockCreate.mockRejectedValue(new ApiError(422, "Validation failed", "Name already exists."));
    await renderReady(ADMIN);

    await act(async () => {
      fireEvent.click(within(screen.getByRole("row", { name: /Grade A/ })).getByRole("button", { name: "deactivate" }));
    });
    expect(screen.getByText(/Server error|boom/)).toBeInTheDocument();

    openCreate();
    fireEvent.change(within(dialog()).getByLabelText("Name *"), { target: { value: "Dup" } });
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Add grade" }));
    });

    expect(within(dialog()).getByText("Name already exists.")).toBeInTheDocument();
    expect(screen.getByText(/Server error|boom/)).toBeInTheDocument(); // still there
  });
});

describe("GradesPage role gating", () => {
  it("renders read-only for a non-admin — no create form, no row actions", async () => {
    await renderReady(WORKER);
    expect(screen.queryByRole("button", { name: "New grade" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "edit" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "deactivate" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "activate" })).not.toBeInTheDocument(); // inactive Legacy row too
  });
});

// ---------------------------------------------------------------------------
// i18n wiring (#182, Task 14, batch B2 — the last B2 screen)
// ---------------------------------------------------------------------------

// `grades` is English-only (not in TRANSLATED_NAMESPACES — see
// translations-status.ts), so under ANY UI language the rendered text falls
// back to this exact English string, same as a still-hardcoded literal would
// render — asserting it, even under a non-English locale, would prove nothing
// (CONTRIBUTING-i18n.md's fallback trap). Swap the catalog value at runtime
// instead, the same i18n.addResource technique the other batches use, so each
// marker only renders if the screen actually reads the catalog rather than a
// literal that happens to still match it.
describe("GradesPage i18n wiring (#182, Task 14)", () => {
  function withOverride(ns: string, key: string, value: string, run: () => Promise<void> | void) {
    const original = i18n.getResource("en", ns, key) as string;
    i18n.addResource("en", ns, key, value);
    return Promise.resolve(run()).finally(() => {
      i18n.addResource("en", ns, key, original);
    });
  }

  it("reads the heading from the catalog, not a hardcoded literal", async () => {
    await withOverride("grades", "title", "TITLE-MARKER", async () => {
      await renderReady(ADMIN);
      expect(screen.getByRole("heading", { name: "TITLE-MARKER" })).toBeInTheDocument();
      expect(screen.queryByRole("heading", { name: "Egg grades" })).not.toBeInTheDocument();
    });
  });

  it("reads the new-grade button label from the catalog, not a hardcoded literal", async () => {
    await withOverride("grades", "newGradeButton", "NEW-GRADE-MARKER", async () => {
      await renderReady(ADMIN);
      expect(screen.getByRole("button", { name: "NEW-GRADE-MARKER" })).toBeInTheDocument();
      expect(screen.queryByRole("button", { name: "New grade" })).not.toBeInTheDocument();
    });
  });

  it("reads the failed-load message from the catalog, not a hardcoded literal", async () => {
    mockList.mockRejectedValue(new Error("boom"));
    await withOverride("grades", "loadGradesFailed", "LOAD-FAILED-MARKER", async () => {
      renderWithProviders(<GradesPage />, { token: ADMIN });
      expect(await screen.findByText("LOAD-FAILED-MARKER")).toBeInTheDocument();
      expect(screen.queryByText("Could not load grades. Is the API up?")).not.toBeInTheDocument();
    });
  });

  // Proves the Type picker and the table's Type cell both read the grade-type
  // ENUM label from the catalog (via gradeTypeLabel), not the raw wire value
  // "Size" or a hardcoded literal — GRADE_A's gradeType is "Size".
  it("reads the grade-type enum label from the catalog for both the picker and the table cell", async () => {
    await withOverride("enums", "gradeType.Size", "SIZE-MARKER", async () => {
      await renderReady(ADMIN);
      const rowA = screen.getByRole("row", { name: /Grade A/ });
      expect(within(rowA).getByText("SIZE-MARKER")).toBeInTheDocument();

      openCreate();
      expect(within(dialog()).getByRole("option", { name: "SIZE-MARKER" })).toBeInTheDocument();
      expect(within(dialog()).queryByRole("option", { name: "Size" })).not.toBeInTheDocument();
    });
  });

  it("reads a table header from the catalog, not a hardcoded literal", async () => {
    await withOverride("grades", "nameHeader", "NAME-HEADER-MARKER", async () => {
      await renderReady(ADMIN);
      expect(screen.getByRole("columnheader", { name: "NAME-HEADER-MARKER" })).toBeInTheDocument();
    });
  });

  it("reads the saleable 'yes' badge from the catalog, not a hardcoded literal", async () => {
    await withOverride("grades", "saleableYesBadge", "YES-MARKER", async () => {
      await renderReady(ADMIN);
      const rowA = screen.getByRole("row", { name: /Grade A/ });
      expect(within(rowA).getByText("YES-MARKER")).toBeInTheDocument();
      expect(within(rowA).queryByText("yes")).not.toBeInTheDocument();
    });
  });
});
