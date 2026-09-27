import { describe, it, expect, vi, beforeEach } from "vitest";
import { screen, within, fireEvent, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { HistoryPage } from "./HistoryPage";
import { renderWithProviders } from "../test/renderWithProviders";
import { stubMatchMedia } from "../test/matchMedia";
import { account, NO_RECORD_HISTORY } from "../test/fixtures";
import {
  listDailyEntries, listEggGrades, listEggUnitConversions, listFlocks,
} from "../api/cluckwork";
import type { DailyEntry, EggGrade, Flock } from "../api/cluckwork";
import { MD_UP_QUERY } from "../lib/breakpoints";

// #980 — the phone surface of History: one two-line row per entry, and a
// Details dialog behind a tap. Its own file rather than cases inside
// HistoryPage.test.tsx, because the two surfaces need opposite answers from
// `useMediaQuery` and a per-case stub would leave the file's default
// ambiguous.
vi.mock("../api/cluckwork", () => ({
  listFlocks: vi.fn(),
  listEggGrades: vi.fn(),
  listEggUnitConversions: vi.fn(),
  listDailyEntries: vi.fn(),
  getDailyEntry: vi.fn(),
  adjustDailyEntry: vi.fn(),
  voidDailyEntry: vi.fn(),
  getFlock: vi.fn(),
  getCustomer: vi.fn(),
}));

const FLOCK: Flock = {
  ...NO_RECORD_HISTORY,
  id: "f1", farmId: "farm1", houseId: "h1", name: "Hen House 1", breed: "ISA",
  placementDate: "2026-01-01", initialCount: 100, currentBirds: 98, status: "Active",
};
const GRADE_A: EggGrade = {
  ...NO_RECORD_HISTORY, id: "gr1", farmId: "farm1", name: "Grade A", gradeType: "Size",
  sortOrder: 1, isSaleable: true, dailyEntryKind: "Manual", active: true, lowStockFloor: null,
};

// Every figure distinct, so a line wired to the wrong field shows a different
// number rather than a plausible one.
const SUBMITTED: DailyEntry = {
  ...NO_RECORD_HISTORY,
  id: "de1", farmId: "farm1", houseId: "h1", flockId: "f1", date: "2026-07-19", status: "Submitted",
  totalEggs: 100, crackedEggs: 2, dirtyEggs: 3, discardedEggs: 5, mortalityCount: 1,
  crackedGradeId: "gr1", dirtyGradeId: null,
  grades: [{ eggGradeId: "gr1", quantity: 40 }],
  version: 1, adjustReason: null, voidReason: null, lockedAtUtc: null, adjustedFrom: null,
  flockName: "Hen House 1", flockStatus: "Active",
};
const DRAFT: DailyEntry = { ...SUBMITTED, id: "de2", date: "2026-07-18", status: "Draft", grades: [] };

const ADMIN = { sub: "u1", role: "Admin" };
const ROW_NAME = /07\/19\/2026/;

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  Element.prototype.scrollIntoView = vi.fn();
  stubMatchMedia(false);
  vi.mocked(listFlocks).mockResolvedValue([FLOCK]);
  vi.mocked(listEggGrades).mockResolvedValue([GRADE_A]);
  vi.mocked(listEggUnitConversions).mockResolvedValue([
    { id: "c1", unitCode: "Individual", eggsPerUnit: 1, active: true, version: 0 },
  ]);
  vi.mocked(listDailyEntries).mockResolvedValue([SUBMITTED]);
});

const row = () => screen.findByRole("button", { name: ROW_NAME });

describe("HistoryPage phone rows (#980)", () => {
  it("asks for the same 900px boundary the shell switches on", async () => {
    const media = stubMatchMedia(false);
    renderWithProviders(<HistoryPage />, { token: ADMIN });
    await row();
    expect(media.matchMedia).toHaveBeenCalledWith(MD_UP_QUERY);
  });

  it("replaces the ten-column ledger with one two-line button per entry", async () => {
    renderWithProviders(<HistoryPage />, { token: ADMIN });
    const button = await row();

    expect(screen.queryByRole("table")).not.toBeInTheDocument();
    // Line 1: date, flock, status. Line 2: total, the three losses, deaths.
    within(button).getByText("Hen House 1");
    within(button).getByText("Submitted");
    within(button).getByText("100");
    within(button).getByText("2/3/5");
    within(button).getByText("1");
    expect(button).toHaveAttribute("aria-haspopup", "dialog");
  });

  it("keeps the desktop ledger above the boundary", async () => {
    stubMatchMedia(true);
    renderWithProviders(<HistoryPage />, { token: ADMIN });
    await screen.findByRole("row", { name: ROW_NAME });
    expect(screen.queryByRole("button", { name: ROW_NAME })).not.toBeInTheDocument();
  });

  it("opens Details on a tap, titled by the entry's date and flock", async () => {
    renderWithProviders(<HistoryPage />, { token: ADMIN });
    fireEvent.click(await row());

    const dialog = await screen.findByRole("dialog", { name: "07/19/2026 · Hen House 1" });
    // The values the two lines leave out, each against its own label.
    for (const [label, value] of [
      ["Cracked", "2"], ["Dirty", "3"], ["Discarded", "5"],
      ["Condition", "2"], ["Graded", "Grade A 40"],
    ]) {
      const term = within(dialog).getByText(label);
      expect(term.nextElementSibling).toHaveTextContent(value);
    }
  });

  it("opens Details from the keyboard and returns focus to the row on Escape", async () => {
    const user = userEvent.setup();
    renderWithProviders(<HistoryPage />, { token: ADMIN });
    const button = await row();
    button.focus();
    await user.keyboard("{Enter}");

    const dialog = await screen.findByRole("dialog");
    await user.keyboard("{Escape}");
    await waitFor(() => expect(dialog).not.toBeInTheDocument());
    await waitFor(() => expect(button).toHaveFocus());
  });

  it("offers an admin the same actions the desktop Actions cell offers", async () => {
    renderWithProviders(<HistoryPage />, { token: ADMIN });
    fireEvent.click(await row());

    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByRole("link", { name: "Audit history" }))
      .toHaveAttribute("href", "/audit?entityId=de1");
    within(dialog).getByRole("button", { name: "adjust" });
    within(dialog).getByRole("button", { name: "void" });
    expect(within(dialog).queryByRole("link", { name: "edit" })).not.toBeInTheDocument();
  });

  it("offers edit, and no correction, on an editable draft", async () => {
    vi.mocked(listDailyEntries).mockResolvedValue([DRAFT]);
    renderWithProviders(<HistoryPage />, { token: ADMIN });
    fireEvent.click(await screen.findByRole("button", { name: /07\/18\/2026/ }));

    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByRole("link", { name: "edit" }))
      .toHaveAttribute("href", "/daily-entry?flockId=f1&date=2026-07-18");
    expect(within(dialog).queryByRole("button", { name: "adjust" })).not.toBeInTheDocument();
    expect(within(dialog).queryByRole("button", { name: "void" })).not.toBeInTheDocument();
  });

  it.each([
    { label: "Sales", token: { sub: "u1", role: "Sales" } },
    { label: "Worker (no role claim)", token: { sub: "u1" } },
  ])("hides the admin-only actions from $label", async ({ token }) => {
    renderWithProviders(<HistoryPage />, { token });
    fireEvent.click(await row());

    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).queryByRole("link", { name: "Audit history" })).not.toBeInTheDocument();
    expect(within(dialog).queryByRole("button", { name: "adjust" })).not.toBeInTheDocument();
    expect(within(dialog).queryByRole("button", { name: "void" })).not.toBeInTheDocument();
  });

  it("replaces Details with the adjust form rather than stacking the two", async () => {
    renderWithProviders(<HistoryPage />, { token: ADMIN });
    fireEvent.click(await row());
    fireEvent.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "adjust" }));

    await screen.findByRole("dialog", { name: "Adjust — 07/19/2026, Hen House 1" });
    // `hidden: true` on purpose: MUI marks a lower dialog aria-hidden, so the
    // default query cannot see a stacked one and would pass either way.
    await waitFor(() => expect(screen.getAllByRole("dialog", { hidden: true })).toHaveLength(1));
  });

  it("mutes a voided row without dropping its figures", async () => {
    vi.mocked(listDailyEntries).mockResolvedValue([
      { ...SUBMITTED, status: "Voided", voidReason: "spoiled" },
    ]);
    renderWithProviders(<HistoryPage />, { token: ADMIN });
    const button = await row();

    expect(button).toHaveStyle({ color: "var(--muted)" });
    within(button).getByText("100");
    within(button).getByText("Voided");
  });
});

// The farm's own date format drives the row's first line and the dialog title,
// so a farm on d/M/y reads its own dates in both (#650).
describe("HistoryPage phone rows and the farm clock", () => {
  it("renders the farm's date format, not the browser's", async () => {
    renderWithProviders(<HistoryPage />, {
      token: ADMIN,
      farm: account({ dateFormatOverride: "dd/MM/yyyy" }),
    });
    fireEvent.click(await screen.findByRole("button", { name: /19\/07\/2026/ }));
    await screen.findByRole("dialog", { name: "19/07/2026 · Hen House 1" });
  });
});
