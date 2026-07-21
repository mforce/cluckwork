import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { DailyEntryPage } from "./DailyEntryPage";
import { listFlocks, listEggGrades, listDailyEntries } from "../api/cluckwork";
import type { Flock, EggGrade } from "../api/cluckwork";

// DailyEntry has no auth/router deps — mock only the API seam it loads from.
vi.mock("../api/cluckwork", () => ({
  listFlocks: vi.fn(),
  listEggGrades: vi.fn(),
  listDailyEntries: vi.fn(),
  recordDailyEntry: vi.fn(),
  submitDailyEntry: vi.fn(),
  createFlock: vi.fn(),
}));

const mockListFlocks = vi.mocked(listFlocks);
const mockListEggGrades = vi.mocked(listEggGrades);
const mockListDailyEntries = vi.mocked(listDailyEntries);

const FLOCK: Flock = {
  id: "f1", farmId: "farm1", houseId: "h1", name: "Hen House 1", breed: "ISA",
  placementDate: "2026-01-01", initialCount: 100, currentBirds: 98, status: "Active",
};
const GRADES: EggGrade[] = [
  { id: "gr1", farmId: "farm1", name: "Grade A", gradeType: "Size", sortOrder: 1, isSaleable: true, active: true },
  { id: "gr2", farmId: "farm1", name: "Grade B", gradeType: "Size", sortOrder: 2, isSaleable: true, active: true },
];

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  mockListFlocks.mockResolvedValue([FLOCK]);
  mockListEggGrades.mockResolvedValue(GRADES);
  mockListDailyEntries.mockResolvedValue([]); // no existing entry for the day
});

function setNum(label: string, value: number) {
  fireEvent.change(screen.getByLabelText(label), { target: { value: String(value) } });
}

async function renderReady() {
  render(<DailyEntryPage />);
  await screen.findByLabelText("Grade A"); // mount load done, grades rendered
  // wait out the prefill fetch so the save buttons aren't disabled by prefillPending
  await waitFor(() => expect(screen.getByRole("button", { name: /Save draft/ })).toBeEnabled());
}

describe("DailyEntryPage accuracy gating", () => {
  it("reports graded-of-sellable and allows submit when within the sellable count", async () => {
    await renderReady();
    setNum("Total eggs", 100);
    setNum("Cracked", 5); // sellable = 95
    setNum("Grade A", 60);
    setNum("Grade B", 30); // graded = 90 ≤ 95

    expect(screen.getByText(/Graded 90 of 95 sellable/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Save & submit/ })).toBeEnabled();
  });

  it("blocks submit (but not draft) when graded exceeds sellable", async () => {
    await renderReady();
    setNum("Total eggs", 100);
    setNum("Cracked", 5); // sellable = 95
    setNum("Grade A", 100); // graded 100 > 95

    expect(screen.getByText(/Graded 100 of 95 sellable/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Save & submit/ })).toBeDisabled();
    expect(screen.getByRole("button", { name: /Save draft/ })).toBeEnabled(); // an over-graded draft is allowed
  });

  it("blocks both saves when cracked+dirty+discarded exceed total eggs", async () => {
    await renderReady();
    setNum("Total eggs", 10);
    setNum("Cracked", 20); // losses 20 > total 10

    expect(screen.getByText(/exceed total eggs \(10\)/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Save & submit/ })).toBeDisabled();
    expect(screen.getByRole("button", { name: /Save draft/ })).toBeDisabled();
  });
});
