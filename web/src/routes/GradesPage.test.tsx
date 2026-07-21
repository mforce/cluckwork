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
  it("creates a grade with the form values, then clears the name", async () => {
    mockCreate.mockResolvedValue({ id: "g3" });
    await renderReady(ADMIN);

    fireEvent.change(screen.getByPlaceholderText("Name *"), { target: { value: "Jumbo" } });
    fireEvent.change(screen.getByLabelText("Sort"), { target: { value: "3" } });
    fireEvent.click(screen.getByLabelText("saleable")); // default true → false
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Add grade" }));
    });

    expect(mockCreate.mock.calls[0][0]).toEqual({
      name: "Jumbo", gradeType: "Size", sortOrder: 3, isSaleable: false,
    });
    expect(screen.getByPlaceholderText("Name *")).toHaveValue(""); // reset on success
  });

  it("saves an inline edit with the edited name/sort/saleable", async () => {
    mockUpdate.mockResolvedValue(undefined);
    await renderReady(ADMIN);

    const rowA = screen.getByRole("row", { name: /Grade A/ });
    fireEvent.click(within(rowA).getByRole("button", { name: "edit" }));
    const editRow = screen.getByRole("row", { name: /Grade A/ });
    fireEvent.change(within(editRow).getByRole("textbox"), { target: { value: "Large" } });
    await act(async () => {
      fireEvent.click(within(editRow).getByRole("button", { name: "save" }));
    });

    expect(mockUpdate.mock.calls[0][0]).toBe("g1");
    expect(mockUpdate.mock.calls[0][1]).toEqual({ name: "Large", sortOrder: 1, isSaleable: true });
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
    const name = () => screen.getByPlaceholderText("Name *");

    fireEvent.change(name(), { target: { value: "One" } });
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Add grade" })); });
    expect(await screen.findByText(/Server error|boom/)).toBeInTheDocument();

    fireEvent.change(name(), { target: { value: "One" } });
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Add grade" })); });

    fireEvent.change(name(), { target: { value: "Two" } });
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Add grade" })); });

    const k1 = mockCreate.mock.calls[0][1];
    const k2 = mockCreate.mock.calls[1][1];
    const k3 = mockCreate.mock.calls[2][1];
    expect(k2).toBe(k1); // failure kept the key → exact replay
    expect(k3).not.toBe(k2); // success rotated it → the next write is fresh
  });
});

describe("GradesPage role gating", () => {
  it("renders read-only for a non-admin — no create form, no row actions", async () => {
    await renderReady(WORKER);
    expect(screen.queryByRole("button", { name: "Add grade" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "edit" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "deactivate" })).not.toBeInTheDocument();
  });
});
