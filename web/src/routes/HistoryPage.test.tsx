import { describe, it, expect, vi, beforeEach } from "vitest";
import { screen, within, fireEvent, act } from "@testing-library/react";
import { HistoryPage } from "./HistoryPage";
import { renderWithProviders } from "../test/renderWithProviders";
import {
  adjustDailyEntry, listDailyEntries, listEggGrades, listFlocks,
} from "../api/cluckwork";
import type { DailyEntry, EggGrade, Flock } from "../api/cluckwork";

// HistoryPage's only runtime dep on the API module is the network seam; mock all
// of it. ApiError comes from ../api/client (unmocked, real) so errText's
// instanceof checks still hold. useAuth + <Link> ride on renderWithProviders.
vi.mock("../api/cluckwork", () => ({
  listFlocks: vi.fn(),
  listEggGrades: vi.fn(),
  listDailyEntries: vi.fn(),
  getDailyEntry: vi.fn(),
  adjustDailyEntry: vi.fn(),
  voidDailyEntry: vi.fn(),
}));

const mockListFlocks = vi.mocked(listFlocks);
const mockListEggGrades = vi.mocked(listEggGrades);
const mockListDailyEntries = vi.mocked(listDailyEntries);
const mockAdjustDailyEntry = vi.mocked(adjustDailyEntry);

const FLOCK: Flock = {
  id: "f1", farmId: "farm1", houseId: "h1", name: "Hen House 1", breed: "ISA",
  placementDate: "2026-01-01", initialCount: 100, currentBirds: 98, status: "Active",
};
const ARCHIVED_FLOCK: Flock = { ...FLOCK, id: "f2", name: "Old Coop", status: "Archived" };
const GRADE_A: EggGrade = { id: "gr1", farmId: "farm1", name: "Grade A", gradeType: "Size", sortOrder: 1, isSaleable: true, active: true };
const GRADE_B: EggGrade = { id: "gr2", farmId: "farm1", name: "Grade B", gradeType: "Size", sortOrder: 2, isSaleable: true, active: true };

// sellable = 100 − 2 − 3 − 5 = 90; two graded lines summing to 60 (within).
const SUBMITTED: DailyEntry = {
  id: "de1", farmId: "farm1", houseId: "h1", flockId: "f1", date: "2026-07-19", status: "Submitted",
  totalEggs: 100, crackedEggs: 2, dirtyEggs: 3, discardedEggs: 5, mortalityCount: 1,
  grades: [{ eggGradeId: "gr1", quantity: 40 }, { eggGradeId: "gr2", quantity: 20 }],
  version: 1, adjustReason: null, voidReason: null, lockedAtUtc: null, adjustedFrom: null,
};
const DRAFT: DailyEntry = { ...SUBMITTED, id: "de2", date: "2026-07-18", status: "Draft", grades: [] };
const DRAFT_ARCHIVED: DailyEntry = { ...DRAFT, id: "de3", flockId: "f2" };

const ADMIN = { sub: "u1", role: "Admin" };

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  // jsdom has no layout engine; keep the stub so any scroll a control triggers
  // (e.g. a browser autoscroll on focus) can't throw mid-test.
  Element.prototype.scrollIntoView = vi.fn();
  mockListFlocks.mockResolvedValue([FLOCK]);
  mockListEggGrades.mockResolvedValue([GRADE_A, GRADE_B]);
  mockListDailyEntries.mockResolvedValue([]);
});

async function openAdjustPanel() {
  renderWithProviders(<HistoryPage />, { token: ADMIN });
  fireEvent.click(await screen.findByRole("button", { name: "adjust" }));
}

describe("HistoryPage dialog dismissal", () => {
  it("closes the adjust dialog on Cancel without writing", async () => {
    mockListDailyEntries.mockResolvedValue([SUBMITTED]);
    await openAdjustPanel();
    fireEvent.change(screen.getByLabelText(/Reason/), { target: { value: "typed then abandoned" } });

    fireEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Cancel" }));

    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(mockAdjustDailyEntry).not.toHaveBeenCalled();
  });
});

describe("HistoryPage adjust — sellable guard", () => {
  it("blocks and warns when the graded lines SUM past sellable (neither line alone over)", async () => {
    mockListDailyEntries.mockResolvedValue([SUBMITTED]);
    await openAdjustPanel();

    // 46 + 45 = 91 > sellable 90, yet neither line individually exceeds 90 —
    // so this only fails if the guard actually SUMS the lines
    fireEvent.change(screen.getByLabelText("Grade A"), { target: { value: "46" } });
    fireEvent.change(screen.getByLabelText("Grade B"), { target: { value: "45" } });
    fireEvent.change(screen.getByLabelText(/Reason/), { target: { value: "recount" } });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Save adjustment" }));
    });

    expect(await screen.findByRole("alert")).toHaveTextContent(/cannot exceed total eggs/);
    expect(mockAdjustDailyEntry).not.toHaveBeenCalled(); // client cap short-circuits the write
  });

  it("submits the corrected lines at the exact boundary sum === sellable (guard is >, not >=)", async () => {
    mockListDailyEntries.mockResolvedValue([SUBMITTED]);
    mockAdjustDailyEntry.mockResolvedValue({ id: "de1", status: "ManagerAdjusted", version: 2 });
    await openAdjustPanel();

    fireEvent.change(screen.getByLabelText("Grade A"), { target: { value: "45" } });
    fireEvent.change(screen.getByLabelText("Grade B"), { target: { value: "45" } }); // 90 === sellable
    fireEvent.change(screen.getByLabelText(/Reason/), { target: { value: "recount" } });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Save adjustment" }));
    });

    expect(await screen.findByRole("status")).toHaveTextContent(/adjusted/);
    const [id, body] = mockAdjustDailyEntry.mock.calls[0];
    expect(id).toBe("de1");
    expect(body).toMatchObject({
      version: 1, totalEggs: 100, crackedEggs: 2, dirtyEggs: 3, discardedEggs: 5, mortalityCount: 1,
      reason: "recount", grades: [{ eggGradeId: "gr1", quantity: 45 }, { eggGradeId: "gr2", quantity: 45 }],
    });
  });
});

describe("HistoryPage draft edit link", () => {
  it("links a draft row to the Daily entry screen with its flock and date in the query", async () => {
    mockListDailyEntries.mockResolvedValue([DRAFT]);
    renderWithProviders(<HistoryPage />, { token: ADMIN });

    const link = await screen.findByRole("link", { name: "edit" });
    expect(link).toHaveAttribute("href", "/daily-entry?flockId=f1&date=2026-07-18");
  });

  it("omits the edit link when the draft's flock is archived", async () => {
    mockListFlocks.mockResolvedValue([ARCHIVED_FLOCK]);
    mockListDailyEntries.mockResolvedValue([DRAFT_ARCHIVED]);
    renderWithProviders(<HistoryPage />, { token: ADMIN });

    // Wait for BOTH the row AND the flock metadata (the filter lists "Old Coop")
    // so the missing link reflects the archived status — not an unrendered row or
    // an unresolved flock (codex review of PR #122 / #86).
    await screen.findByText("2026-07-18");
    await screen.findByRole("option", { name: "Old Coop" });
    expect(screen.queryByRole("link", { name: "edit" })).not.toBeInTheDocument();
  });
});

describe("HistoryPage role gating", () => {
  // adjust/void are gated on isAdmin = Admin || Manager (claims.ts); every other
  // role — including a plain Worker with no role claim — sees neither control.
  it.each([
    { label: "Admin", token: { sub: "u1", role: "Admin" }, allowed: true },
    { label: "Manager", token: { sub: "u1", role: "Manager" }, allowed: true },
    { label: "Sales", token: { sub: "u1", role: "Sales" }, allowed: false },
    { label: "ReadOnly", token: { sub: "u1", role: "ReadOnly" }, allowed: false },
    { label: "Worker (no role claim)", token: { sub: "u1" }, allowed: false },
  ])("$label sees the adjust/void controls: $allowed", async ({ token, allowed }) => {
    mockListDailyEntries.mockResolvedValue([SUBMITTED]);
    renderWithProviders(<HistoryPage />, { token });

    await screen.findByText("2026-07-19"); // the submitted (correctable) row
    const adjust = screen.queryByRole("button", { name: "adjust" });
    const voidBtn = screen.queryByRole("button", { name: "void" });
    if (allowed) {
      expect(adjust).toBeInTheDocument();
      expect(voidBtn).toBeInTheDocument();
    } else {
      expect(adjust).not.toBeInTheDocument();
      expect(voidBtn).not.toBeInTheDocument();
    }
  });
});
