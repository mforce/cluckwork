import { describe, it, expect, vi, beforeEach } from "vitest";
import { screen, fireEvent, act } from "@testing-library/react";
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

// sellable = 100 − 2 − 3 − 5 = 90; grades sum 60 (within).
const SUBMITTED: DailyEntry = {
  id: "de1", farmId: "farm1", houseId: "h1", flockId: "f1", date: "2026-07-19", status: "Submitted",
  totalEggs: 100, crackedEggs: 2, dirtyEggs: 3, discardedEggs: 5, mortalityCount: 1,
  grades: [{ eggGradeId: "gr1", quantity: 60 }],
  version: 1, adjustReason: null, voidReason: null, lockedAtUtc: null, adjustedFrom: null,
};
const DRAFT: DailyEntry = { ...SUBMITTED, id: "de2", date: "2026-07-18", status: "Draft", grades: [] };
const DRAFT_ARCHIVED: DailyEntry = { ...DRAFT, id: "de3", flockId: "f2" };

const ADMIN = { sub: "u1", role: "Admin" };
const WORKER = { sub: "u1" }; // no role claim → Worker → not admin

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  // jsdom has no layout engine; the panel's focus effect calls scrollIntoView.
  Element.prototype.scrollIntoView = vi.fn();
  mockListFlocks.mockResolvedValue([FLOCK]);
  mockListEggGrades.mockResolvedValue([GRADE_A]);
  mockListDailyEntries.mockResolvedValue([]);
});

describe("HistoryPage adjust — sellable guard", () => {
  it("blocks the adjustment and warns when graded quantities exceed sellable", async () => {
    mockListDailyEntries.mockResolvedValue([SUBMITTED]);
    renderWithProviders(<HistoryPage />, { token: ADMIN });
    fireEvent.click(await screen.findByRole("button", { name: "adjust" }));

    // sellable is 90; push Grade A past it
    fireEvent.change(screen.getByLabelText("Grade A"), { target: { value: "95" } });
    fireEvent.change(screen.getByLabelText(/Reason/), { target: { value: "recount" } });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Save adjustment" }));
    });

    expect(await screen.findByRole("alert")).toHaveTextContent(/cannot exceed total eggs/);
    expect(mockAdjustDailyEntry).not.toHaveBeenCalled(); // the client-side cap short-circuits the write
  });

  it("submits the corrected grade lines when the sum is within sellable", async () => {
    mockListDailyEntries.mockResolvedValue([SUBMITTED]);
    mockAdjustDailyEntry.mockResolvedValue({ id: "de1", status: "ManagerAdjusted", version: 2 });
    renderWithProviders(<HistoryPage />, { token: ADMIN });
    fireEvent.click(await screen.findByRole("button", { name: "adjust" }));

    fireEvent.change(screen.getByLabelText("Grade A"), { target: { value: "80" } }); // ≤ 90
    fireEvent.change(screen.getByLabelText(/Reason/), { target: { value: "recount" } });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Save adjustment" }));
    });

    expect(await screen.findByRole("status")).toHaveTextContent(/adjusted/);
    const [id, body] = mockAdjustDailyEntry.mock.calls[0];
    expect(id).toBe("de1");
    expect(body).toMatchObject({
      version: 1, totalEggs: 100, crackedEggs: 2, dirtyEggs: 3, discardedEggs: 5,
      reason: "recount", grades: [{ eggGradeId: "gr1", quantity: 80 }],
    });
  });
});

describe("HistoryPage draft edit link", () => {
  it("links a draft row to the Daily entry screen prefilled with its flock and date", async () => {
    mockListDailyEntries.mockResolvedValue([DRAFT]);
    renderWithProviders(<HistoryPage />, { token: ADMIN });

    const link = await screen.findByRole("link", { name: "edit" });
    expect(link).toHaveAttribute("href", "/daily-entry?flockId=f1&date=2026-07-18");
  });

  it("omits the edit link when the draft's flock is archived", async () => {
    mockListFlocks.mockResolvedValue([ARCHIVED_FLOCK]);
    mockListDailyEntries.mockResolvedValue([DRAFT_ARCHIVED]);
    renderWithProviders(<HistoryPage />, { token: ADMIN });

    await screen.findByText("2026-07-18"); // the row rendered
    // capture excludes archived flocks, so an edit link would fall back to the
    // wrong flock — better to render none (codex review of #86)
    expect(screen.queryByRole("link", { name: "edit" })).not.toBeInTheDocument();
  });
});

describe("HistoryPage role gating", () => {
  it("hides adjust and void controls from a non-admin", async () => {
    mockListDailyEntries.mockResolvedValue([SUBMITTED]);
    renderWithProviders(<HistoryPage />, { token: WORKER });

    await screen.findByText("2026-07-19"); // the submitted row rendered
    expect(screen.queryByRole("button", { name: "adjust" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "void" })).not.toBeInTheDocument();
  });
});
