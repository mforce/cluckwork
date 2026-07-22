import { describe, it, expect, vi, beforeEach } from "vitest";
import { screen, within, fireEvent, act } from "@testing-library/react";
import { GradesPage } from "./GradesPage";
import { renderWithProviders } from "../test/renderWithProviders";
import {
  activateEggGrade, createEggGrade, deactivateEggGrade, listEggGrades, updateEggGrade,
} from "../api/cluckwork";
import type { EggGrade } from "../api/cluckwork";
import { ApiError } from "../api/client";

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

const GRADE_A: EggGrade = { id: "g1", farmId: "f", name: "Grade A", gradeType: "Size", sortOrder: 1, isSaleable: true, active: true };
const GRADE_OLD: EggGrade = { id: "g2", farmId: "f", name: "Legacy", gradeType: "Quality", sortOrder: 2, isSaleable: false, active: false };

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
    // the dialog is seeded from the row, then all three fields move off it
    expect(within(dialog()).getByLabelText("Name *")).toHaveValue("Grade A");
    fireEvent.change(within(dialog()).getByLabelText("Name *"), { target: { value: "Large" } });
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

describe("GradesPage role gating", () => {
  it("renders read-only for a non-admin — no create form, no row actions", async () => {
    await renderReady(WORKER);
    expect(screen.queryByRole("button", { name: "New grade" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "edit" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "deactivate" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "activate" })).not.toBeInTheDocument(); // inactive Legacy row too
  });
});
