import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, fireEvent, waitFor, act } from "@testing-library/react";
import { DailyEntryPage } from "./DailyEntryPage";
import { listFlocks, listEggGrades, listDailyEntries } from "../api/cluckwork";
import type { Flock, EggGrade, DailyEntry } from "../api/cluckwork";
import { todayIso } from "../lib/dates";

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

function saveDraftBtn() {
  return screen.getByRole("button", { name: /Save draft/ });
}
function submitBtn() {
  return screen.getByRole("button", { name: /Save & submit/ });
}
function gradedMessage() {
  return screen.getByText(/Graded \d+ of \d+ sellable/);
}

async function renderReady() {
  render(<DailyEntryPage />);
  await screen.findByLabelText("Grade A"); // mount load done, grades rendered
  // wait out the prefill fetch (it gates the save buttons via prefillPending)
  await waitFor(() => expect(saveDraftBtn()).toBeEnabled());
  expect(mockListDailyEntries).toHaveBeenCalled(); // prefill really ran
}

describe("DailyEntryPage accuracy gating", () => {
  it("reports graded-of-sellable (muted) and allows submit when within sellable", async () => {
    await renderReady();
    setNum("Total eggs", 100);
    setNum("Cracked", 2);
    setNum("Dirty", 3);
    setNum("Discarded", 5); // losses 10 → sellable 90 (all three counted)
    setNum("Grade A", 60);
    setNum("Grade B", 25); // graded 85 ≤ 90

    expect(gradedMessage()).toHaveTextContent("Graded 85 of 90 sellable");
    expect(gradedMessage()).toHaveClass("muted"); // within → not the error style
    expect(submitBtn()).toBeEnabled();
  });

  it("blocks submit (but not draft) and styles the message error when graded exceeds sellable", async () => {
    await renderReady();
    setNum("Total eggs", 100);
    setNum("Cracked", 2);
    setNum("Dirty", 3);
    setNum("Discarded", 5); // sellable 90
    setNum("Grade A", 95); // graded 95 > 90

    expect(gradedMessage()).toHaveTextContent("Graded 95 of 90 sellable");
    expect(gradedMessage()).toHaveClass("error");
    expect(submitBtn()).toBeDisabled();
    expect(saveDraftBtn()).toBeEnabled(); // an over-graded draft is allowed
  });

  it("allows submit at the exact boundary graded === sellable (the gate is >, not >=)", async () => {
    await renderReady();
    setNum("Total eggs", 100);
    setNum("Cracked", 2);
    setNum("Dirty", 3);
    setNum("Discarded", 5); // sellable 90
    setNum("Grade A", 90); // graded 90 === 90

    expect(gradedMessage()).toHaveTextContent("Graded 90 of 90 sellable");
    expect(gradedMessage()).toHaveClass("muted");
    expect(submitBtn()).toBeEnabled();
  });

  it("blocks both saves when cracked+dirty+discarded exceed total eggs", async () => {
    await renderReady();
    setNum("Total eggs", 10);
    setNum("Cracked", 2);
    setNum("Dirty", 3);
    setNum("Discarded", 6); // losses 11 > 10

    const msg = screen.getByText(/exceed total eggs \(10\)/);
    expect(msg).toHaveTextContent("Cracked + dirty + discarded (11) exceed total eggs (10)");
    expect(msg).toHaveClass("error");
    expect(submitBtn()).toBeDisabled();
    expect(saveDraftBtn()).toBeDisabled();
  });

  it("allows both saves at the exact boundary losses === total (zero graded)", async () => {
    await renderReady();
    setNum("Total eggs", 10);
    setNum("Cracked", 4);
    setNum("Dirty", 3);
    setNum("Discarded", 3); // losses 10 === total → sellable 0, graded 0

    expect(gradedMessage()).toHaveTextContent("Graded 0 of 0 sellable");
    expect(submitBtn()).toBeEnabled();
    expect(saveDraftBtn()).toBeEnabled();
  });
});

describe("DailyEntryPage prefill gating", () => {
  it("blocks saving until the prefill lookup resolves", async () => {
    let resolvePrefill!: (entries: DailyEntry[]) => void;
    mockListDailyEntries.mockReturnValue(new Promise((r) => (resolvePrefill = r)));
    render(<DailyEntryPage />);
    await screen.findByLabelText("Grade A"); // mount load done

    // prefill in flight → both saves disabled (no overwrite of an unknown day)
    await waitFor(() => expect(saveDraftBtn()).toBeDisabled());
    expect(mockListDailyEntries).toHaveBeenCalledWith(
      expect.objectContaining({ flockId: "f1" }),
    );

    await act(async () => resolvePrefill([]));
    expect(saveDraftBtn()).toBeEnabled();
  });

  it("locks the form when the day already has a submitted entry", async () => {
    const existing: DailyEntry = {
      id: "de1", farmId: "farm1", houseId: "h1", flockId: "f1", date: todayIso(), status: "Submitted",
      totalEggs: 100, crackedEggs: 2, dirtyEggs: 3, discardedEggs: 5, mortalityCount: 1,
      grades: [{ eggGradeId: "gr1", quantity: 60 }, { eggGradeId: "gr2", quantity: 25 }],
      version: 1, adjustReason: null, voidReason: null, lockedAtUtc: "2026-07-20T10:00:00Z", adjustedFrom: null,
    };
    mockListDailyEntries.mockResolvedValue([existing]);
    render(<DailyEntryPage />);

    // the lock banner appears once the prefill applies the existing status
    expect(await screen.findByText(/already submitted/)).toBeInTheDocument();
    expect(screen.getByLabelText("Total eggs")).toBeDisabled();
    expect(submitBtn()).toBeDisabled();
    expect(saveDraftBtn()).toBeDisabled();
  });
});
