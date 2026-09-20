// web/src/routes/Dashboard.test.tsx
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Dashboard } from "./Dashboard";
import { renderWithProviders } from "../test/renderWithProviders";
import {
  getProductionReport, getStock, listDailyEntries, listFlocks, listOrders,
} from "../api/cluckwork";
import type { DailyEntry, Flock, ProductionDay, ProductionReport, SalesOrder, StockRow } from "../api/cluckwork";
import { daysBefore, todayIso } from "../lib/dates";
import i18n from "../i18n";
import { NO_RECORD_HISTORY, account } from "../test/fixtures";
import { stubMatchMedia } from "../test/matchMedia";

// Keep the real formatters; stub the six read endpoints the dashboard fans out.
vi.mock("../api/cluckwork", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api/cluckwork")>();
  return {
    ...actual,
    listFlocks: vi.fn(),
    listDailyEntries: vi.fn(),
    getStock: vi.fn(),
    listOrders: vi.fn(),
    getProductionReport: vi.fn(),
  };
});

const mockFlocks = vi.mocked(listFlocks);
const mockEntries = vi.mocked(listDailyEntries);
const mockStock = vi.mocked(getStock);
const mockOrders = vi.mocked(listOrders);
const mockReport = vi.mocked(getProductionReport);

const flock = (id: string, status: string): Flock => ({
  ...NO_RECORD_HISTORY,
  id, farmId: "f", houseId: "h", name: `Flock ${id}`, breed: "ISA",
  placementDate: "2026-01-01", initialCount: 100, currentBirds: 98, status,
});
const entry = (flockId: string, status: string, totalEggs: number): DailyEntry => ({
  ...NO_RECORD_HISTORY,
  id: `de-${flockId}`, farmId: "f", houseId: "h", flockId, date: "2026-07-21", status,
  totalEggs, crackedEggs: 0, dirtyEggs: 0, discardedEggs: 0, mortalityCount: 0,
  crackedGradeId: null, dirtyGradeId: null, grades: [],
  version: 1, adjustReason: null, voidReason: null, lockedAtUtc: null, adjustedFrom: null,
});
// One house that recorded — the ordinary complete day. `recordedFlocks: 0` is
// the day nobody recorded, and `recordedFlocks < expectedFlocks` the day only
// some houses did; both used to arrive indistinguishable from a real zero.
const day = (date: string, totalEggs: number, recordedFlocks = 1, expectedFlocks = 1): ProductionDay => ({
  date, totalEggs, cracked: 0, dirty: 0, discarded: 0, sellable: totalEggs, fromCounts: 0,
  deaths: 0, recordedFlocks, expectedFlocks,
  missingFlocks: Math.max(0, expectedFlocks - recordedFlocks),
  henDays: 100,
  recordedHenDays: expectedFlocks > 0 ? Math.round((100 * recordedFlocks) / expectedFlocks) : 0,
  ratedEggs: recordedFlocks > 0 ? totalEggs : 0,
  henDayPct: recordedFlocks > 0 ? totalEggs : null,
});
const report = (periodHenDayPct: number | null, days: ProductionDay[]): ProductionReport => ({
  days, totalEggs: 0, totalSellable: 0, totalFromCounts: 0, totalDeaths: 0, totalHenDays: 0, totalRecordedHenDays: 0, totalRatedEggs: 0,
  periodHenDayPct, gradeTotals: [],
});
const STOCK: StockRow[] = [
  { eggGradeId: "g1", gradeName: "Grade A", available: 1240, restricted: 0 },
  { eggGradeId: "g2", gradeName: "Grade B", available: 320, restricted: 0 },
];
const order = (id: string, ref: string, customerName: string | null): SalesOrder => ({
  ...NO_RECORD_HISTORY, id, customerId: "c1", customerName, referenceNumber: ref,
  orderDate: "2026-07-21", status: "Draft", totalMinorUnits: 1000, currencyCode: "USD",
  currencyMinorUnit: 2, voidReason: null, discountReasonCode: null, discountReasonNote: null,
  outstandingMinorUnits: null, items: [],
});

// The report mock answers by window (relative to a given "today") so the two
// calls can be told apart. Values: previous week 307..301, current week 327..321.
const previousFor = (today: string) => report(85.1, [7, 6, 5, 4, 3, 2, 1].map((n) => day(daysBefore(today, n + 7), 300 + n)));
const currentFor = (today: string) => report(87.4, [7, 6, 5, 4, 3, 2, 1].map((n) => day(daysBefore(today, n), 320 + n)));
const reportByWindow = (today: string) => (from: string, to: string) => {
  if (from === daysBefore(today, 7) && to === daysBefore(today, 1)) return Promise.resolve(currentFor(today));
  if (from === daysBefore(today, 14) && to === daysBefore(today, 8)) return Promise.resolve(previousFor(today));
  return Promise.reject(new Error(`unexpected window ${from}..${to}`));
};

// Outside a FarmProvider useFarmToday() computes today browser-local — the
// same value the screen uses, so the report-call oracle below is exact. The
// farm-scoped test at the bottom is the one that proves the FARM's day wins.
const today = todayIso();

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  mockFlocks.mockResolvedValue([flock("f1", "Active"), flock("f2", "Active"), flock("f3", "Active")]);
  mockEntries.mockResolvedValue([entry("f1", "Submitted", 178), entry("f2", "Voided", 999)]);
  mockStock.mockResolvedValue(STOCK);
  mockOrders.mockResolvedValue([] as SalesOrder[]);
  mockReport.mockImplementation(reportByWindow(today));
});

// The page renders "Loading…" until every read settles, so a panel is found,
// never got. #829 — sections are no longer `.panel` cards, just a `<section>`
// under a ruled `h3` (MUI `Typography variant="h3"`, which the theme maps to
// a real `<h3>` element).
const panel = async (title: string) =>
  (await screen.findByRole("heading", { name: title, level: 3 })).closest("section") as HTMLElement;

// A Today row's accessible group — `role="group"` named after the flock, so
// a row's status/action/count (siblings of the name link, not nested inside
// it) can be queried together without depending on layout classNames.
const todayRow = (flockName: string) => screen.getByRole("group", { name: flockName });

// The total row's label ("Today so far") and its numeral are separate
// elements (#883 round 4, finding D — the mockup pairs a fixed label with a
// numeral beside it, never a sentence built by interpolating the figure into
// the label). A bare numeral like "178" also appears on individual Today
// rows, so this scopes the read to the label's own sibling rather than an
// unscoped `getByText`, which would be ambiguous whenever a row's count
// happens to match the total.
const todayTotal = async () => {
  const label = await screen.findByText("Today so far");
  return label.parentElement?.querySelector(".num")?.textContent;
};

function withOverride(ns: string, key: string, value: string, run: () => Promise<void> | void) {
  const original = i18n.getResource("en", ns, key) as string;
  i18n.addResource("en", ns, key, value);
  return Promise.resolve(run()).finally(() => { i18n.addResource("en", ns, key, original); });
}

describe("Dashboard capture status (#654, #829 ruled list)", () => {
  it("renders one row per active flock, no-entry rows first, each linking to that flock's entry for today", async () => {
    renderWithProviders(<Dashboard />);
    const f1link = await screen.findByRole("link", { name: "Flock f1: open today's entry" });
    expect(f1link).toHaveAttribute("href", `/daily-entry?flockId=f1&date=${today}`);
    const f1row = todayRow("Flock f1");
    expect(within(f1row).getByText("178")).toBeInTheDocument();
    expect(within(f1row).getByText("Submitted")).toBeInTheDocument();
    const names = screen.getAllByRole("link", { name: /open today's entry/ }).map((a) => a.getAttribute("aria-label"));
    expect(names).toEqual(["Flock f2: no entry yet, open today's entry", "Flock f3: no entry yet, open today's entry", "Flock f1: open today's entry"]);
  });

  it("marks a flock with no entry — and one whose only entry is Voided — as missing (#82)", async () => {
    renderWithProviders(<Dashboard />);
    await screen.findByRole("link", { name: "Flock f2: no entry yet, open today's entry" });
    for (const id of ["f2", "f3"]) {
      const row = todayRow(`Flock ${id}`);
      expect(within(row).getByRole("link", { name: `Record Flock ${id}` })).toBeInTheDocument();
      expect(within(row).getByText("Not recorded")).toBeInTheDocument();
      expect(within(row).queryByText("—")).not.toBeInTheDocument();
      expect(within(row).queryByText("999")).not.toBeInTheDocument();
    }
    expect(within(todayRow("Flock f1")).queryByRole("link", { name: /^Record/ })).not.toBeInTheDocument();
  });

  it("keeps a missing house's short Record action in the count column on the name row", async () => {
    renderWithProviders(<Dashboard />);
    const action = await screen.findByRole("link", { name: "Record Flock f2" });
    const row = todayRow("Flock f2");
    expect(action).toHaveTextContent(/^Record$/);
    expect(action).toHaveClass("MuiButton-outlined", "MuiButton-colorInherit");
    expect(row.children).toHaveLength(3);
    expect(row.children[2]).toBe(action);
    expect(action).toHaveStyle({ gridColumn: "3", gridRow: "1", minHeight: "44px" });
    expect(within(row).queryByText("—")).not.toBeInTheDocument();
  });

  it("offers 'Record today' on hover for a row with no entry, and not on one that has an entry", async () => {
    renderWithProviders(<Dashboard />);
    const missing = await screen.findByRole("link", { name: "Flock f3: no entry yet, open today's entry" });
    expect(missing).toHaveAttribute("title", "Record today");
    // A recorded flock has nothing to record, so it carries no hint at all.
    expect(screen.getByRole("link", { name: "Flock f1: open today's entry" })).not.toHaveAttribute("title");
  });

  it("announces the missing state in the row's accessible name, not only in its colour and badge", async () => {
    renderWithProviders(<Dashboard />);
    // `aria-label` overrides the link's inner content, so the visible "no entry"
    // badge is NOT part of the accessible name — the name has to carry it.
    expect(await screen.findByRole("link", { name: "Flock f3: no entry yet, open today's entry" })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Flock f3: open today's entry" })).not.toBeInTheDocument();
    // A recorded flock keeps the plain name.
    expect(screen.getByRole("link", { name: "Flock f1: open today's entry" })).toBeInTheDocument();
  });

  it("reads the short Record label from the catalog while retaining the house in its accessible name", async () => {
    await withOverride("dashboard", "recordAction", "SHORT-RECORD", async () => {
      renderWithProviders(<Dashboard />);
      expect(await screen.findByRole("link", { name: "Record Flock f2" })).toHaveTextContent("SHORT-RECORD");
    });
  });

  it("reads the hover text from the catalog, not a hardcoded literal", async () => {
    await withOverride("dashboard", "recordTodayHint", "RECORD-MARKER", async () => {
      renderWithProviders(<Dashboard />);
      const missing = await screen.findByRole("link", { name: "Flock f3: no entry yet, open today's entry" });
      expect(missing).toHaveAttribute("title", "RECORD-MARKER");
    });
  });

  it("reads the Record action's label from the catalog, not a hardcoded literal", async () => {
    await withOverride("dashboard", "recordHouseAction", "RECORD-ACTION {{flock}}", async () => {
      renderWithProviders(<Dashboard />);
      await screen.findByRole("link", { name: "Flock f2: no entry yet, open today's entry" });
      expect(within(todayRow("Flock f2")).getByRole("link", { name: "RECORD-ACTION Flock f2" })).toBeInTheDocument();
    });
  });

  it("gives a Draft entry a ruled-text Continue action; a Submitted one none", async () => {
    mockEntries.mockResolvedValue([entry("f1", "Submitted", 178), entry("f2", "Draft", 40)]);
    renderWithProviders(<Dashboard />);
    await screen.findByRole("link", { name: "Flock f1: open today's entry" });
    expect(within(todayRow("Flock f2")).getByRole("link", { name: "Continue Flock f2" })).toBeInTheDocument();
    expect(within(todayRow("Flock f1")).queryByRole("link", { name: /^Continue|^Record/ })).not.toBeInTheDocument();
  });

  // #883 round 5 — the owner's read of the PR's screenshots found the
  // Continue action rendering as bold, brand-coloured text on both widths:
  // `Button variant="text"` reads MUI's default text-button styling (bold,
  // primary colour), not the ruled-text row action DIRECTION.md line 7 calls
  // for. The fix drops the Button and reuses the same Typography+Link pattern
  // the sales row's "Review to confirm" action already renders with (below),
  // so this asserts against THAT class rather than inventing a new one: a
  // `MuiTypography-body2` element, never a `MuiButtonBase`/`MuiButton` one.
  it("renders Continue as MUI Typography ruled text, not a MuiButton (#883 round 5)", async () => {
    mockEntries.mockResolvedValue([entry("f2", "Draft", 40)]);
    renderWithProviders(<Dashboard />);
    const action = await screen.findByRole("link", { name: "Continue Flock f2" });
    expect(action.className, "Continue should render as ruled Typography text").toMatch(/\bMuiTypography-body2\b/);
    expect(action.className, "Continue should carry no Button chrome").not.toMatch(/MuiButton/);
  });

  it("sums today's eggs excluding the Voided entry — 178, never 1,177", async () => {
    renderWithProviders(<Dashboard />);
    expect(await todayTotal()).toBe("178");
    expect(screen.queryByText(/1,177/)).not.toBeInTheDocument();
  });

  it("caps the list at 12 rows, the missing ones first, and links the rest (INV-9)", async () => {
    mockFlocks.mockResolvedValue(Array.from({ length: 15 }, (_, i) => flock(`f${i}`, "Active")));
    mockEntries.mockResolvedValue(Array.from({ length: 12 }, (_, i) => entry(`f${i}`, "Submitted", 1))); // f12..f14 missing
    renderWithProviders(<Dashboard />);
    const more = await screen.findByRole("link", { name: "3 more flocks" });
    expect(more).toHaveAttribute("href", "/daily-entry");
    const rowLinks = screen.getAllByRole("link", { name: /open today's entry/ });
    expect(rowLinks).toHaveLength(12);
    const missingNames = rowLinks.slice(0, 3).map((t) => t.getAttribute("aria-label"));
    expect(missingNames).toEqual(["Flock f12: no entry yet, open today's entry", "Flock f13: no entry yet, open today's entry", "Flock f14: no entry yet, open today's entry"]);
    expect(["f12", "f13", "f14"].every((id) =>
      within(todayRow(`Flock ${id}`)).queryByRole("link", { name: `Record Flock ${id}` }) !== null)).toBe(true);
  });
});

// #883 round 2, finding 4 — DIRECTION.md line 6: the entry state WITH its
// time ("Recorded 06:40", "Draft, saved 06:52"), farm-local. A house with no
// entry yet carries no time at all — there is nothing to have recorded.
describe("Dashboard Today row entry state time (#883 round 2, finding 4)", () => {
  const farm = account({ timeZoneId: "UTC" });

  it("shows a recorded entry's own time, and a draft's own save time, both farm-local", async () => {
    mockEntries.mockResolvedValue([
      { ...entry("f1", "Submitted", 178), madeOfficialAtUtc: "2026-07-21T06:40:00Z" },
      { ...entry("f2", "Draft", 40), createdAtUtc: "2026-07-21T05:00:00Z", lastChangedAtUtc: "2026-07-21T06:52:00Z" },
    ]);
    renderWithProviders(<Dashboard />, { farm });
    await screen.findByRole("link", { name: "Flock f1: open today's entry" });
    expect(within(todayRow("Flock f1")).getByText("Recorded 06:40")).toBeInTheDocument();
    expect(within(todayRow("Flock f2")).getByText("Draft, saved 06:52")).toBeInTheDocument();
  });

  it("shows no time on a house with no entry yet", async () => {
    renderWithProviders(<Dashboard />, { farm });
    await screen.findByRole("link", { name: "Flock f2: no entry yet, open today's entry" });
    expect(within(todayRow("Flock f2")).queryByText(/^Recorded|^Draft,/)).not.toBeInTheDocument();
    expect(within(todayRow("Flock f3")).queryByText(/^Recorded|^Draft,/)).not.toBeInTheDocument();
  });

  it("falls back to the bare status word when the record has no timestamp to show", async () => {
    // The default `entry()` fixture (NO_RECORD_HISTORY, no madeOfficialAtUtc)
    // — data predating #494, or a fixture that doesn't care.
    renderWithProviders(<Dashboard />, { farm });
    await screen.findByRole("link", { name: "Flock f1: open today's entry" });
    expect(within(todayRow("Flock f1")).getByText("Submitted")).toBeInTheDocument();
  });
});

// #829/#864 — the attention line: one line, missing houses only, folding
// past the attention cap into a count. The desktop-only "Needs attention"
// list combining a second data source (stock floors) was proposed on #864
// and not taken — this line has exactly one source, so it renders nothing
// when every house is in.
describe("Dashboard attention line (#829, #864)", () => {
  it("renders nothing when every house has an entry", async () => {
    mockEntries.mockResolvedValue([entry("f1", "Submitted", 178), entry("f2", "Submitted", 1), entry("f3", "Submitted", 1)]);
    renderWithProviders(<Dashboard />);
    expect(await todayTotal()).toBe("180");
    expect(screen.queryByText(/not recorded/)).not.toBeInTheDocument();
  });

  it("names a single missing house with no fold", async () => {
    mockFlocks.mockResolvedValue([flock("f1", "Active"), flock("f2", "Active")]);
    mockEntries.mockResolvedValue([entry("f1", "Submitted", 178)]); // f2 missing
    renderWithProviders(<Dashboard />);
    expect(await screen.findByText("Flock f2 not recorded")).toBeInTheDocument();
    expect(screen.queryByText(/\+\d+ more/)).not.toBeInTheDocument();
  });

  it("shows two missing houses ruled apart at desktop width (md and up), and folds the rest into a count", async () => {
    stubMatchMedia(true); // >= 900px, the md breakpoint AppLayout/BottomNav switch on
    mockFlocks.mockResolvedValue(Array.from({ length: 4 }, (_, i) => flock(`f${i}`, "Active")));
    mockEntries.mockResolvedValue([]); // every one of the four is missing
    renderWithProviders(<Dashboard />);
    expect(await screen.findByText("Flock f0 not recorded")).toBeInTheDocument();
    expect(screen.getByText("Flock f1 not recorded")).toBeInTheDocument();
    expect(screen.queryByText("Flock f2 not recorded")).not.toBeInTheDocument();
    expect(screen.getByRole("link", { name: "+2 more" })).toHaveAttribute("href", "/daily-entry");
  });

  // #883 round 2, finding 1: DIRECTION.md's fold point is two items at 1280
  // and ONE at 390 — a flat cap of 2 overcounted on a phone. Below md (900px)
  // MUI's `useMediaQuery` resolves to its `defaultMatches` (false) when
  // `matchMedia` is left unstubbed, so this is also what a test gets by doing
  // nothing — asserted explicitly here rather than left implicit.
  it("shows exactly one missing house below the md breakpoint, and folds the rest into a count", async () => {
    stubMatchMedia(false); // < 900px
    mockFlocks.mockResolvedValue(Array.from({ length: 4 }, (_, i) => flock(`f${i}`, "Active")));
    mockEntries.mockResolvedValue([]);
    renderWithProviders(<Dashboard />);
    expect(await screen.findByText("Flock f0 not recorded")).toBeInTheDocument();
    expect(screen.queryByText("Flock f1 not recorded")).not.toBeInTheDocument();
    expect(screen.getByRole("link", { name: "+3 more" })).toHaveAttribute("href", "/daily-entry");
  });

  // CodeRabbit, PR #883 round 1: missingHouses and the "N of M houses in"
  // caption were both derived from `tiles.shown`, the list `visibleTiles`
  // caps at 12 — so a farm with more than 12 missing houses undercounted
  // both, since every one of the 12 shown was itself missing (missing-first
  // ordering) and nothing past the cap was ever counted. They now come from
  // the FULL, uncapped capture-status list.
  it("counts every missing house, not only the 12 visibleTiles caps the row list at", async () => {
    stubMatchMedia(true); // desktop cap (2 shown) — this test is about the COUNT, not the width
    mockFlocks.mockResolvedValue(Array.from({ length: 15 }, (_, i) => flock(`f${i}`, "Active")));
    mockEntries.mockResolvedValue([]); // all 15 missing
    renderWithProviders(<Dashboard />);
    await screen.findByText("Flock f0 not recorded");
    // 15 missing, 2 shown ruled apart, so the fold count is 13 — not 10,
    // which is what `tiles.shown.length` (capped at 12) minus 2 would give.
    expect(screen.getByRole("link", { name: "+13 more" })).toBeInTheDocument();
    expect(screen.getByText("0 of 15 houses in")).toBeInTheDocument();
  });

  // CodeRabbit, PR #883 round 1: a single-key catalog string could only ever
  // render "houses" — i18next selects the plural on `{{count}}`, and the
  // caption now carries `_one`/`_other` forms.
  it("says 'house', singular, when the farm has exactly one", async () => {
    mockFlocks.mockResolvedValue([flock("f1", "Active")]);
    mockEntries.mockResolvedValue([entry("f1", "Submitted", 1)]);
    renderWithProviders(<Dashboard />);
    expect(await screen.findByText("1 of 1 house in")).toBeInTheDocument();
  });
});

// #864 owner amendment (2026-09-16) — a reference under the running total,
// read from the 14-day strip's own last (yesterday) slot: no second fetch,
// and no figure at all when yesterday was not a complete day.
describe("Dashboard 'Yesterday by close' caption (#864)", () => {
  it("shows yesterday's total when the strip's last day is complete", async () => {
    renderWithProviders(<Dashboard />);
    // currentFor maps n=7..1 to daysBefore(today,n) with value 320+n, so the
    // window's last day — daysBefore(today,1), yesterday — is 320+1 = 321.
    expect(await screen.findByText("Yesterday by close: 321")).toBeInTheDocument();
  });

  it("shows no caption when yesterday was not fully recorded", async () => {
    mockReport.mockImplementation((from, to) =>
      reportByWindow(today)(from, to).then((r) => (to === daysBefore(today, 1)
        ? { ...r, days: r.days.map((d, i) => (i === 6 ? { ...d, recordedFlocks: 0, missingFlocks: d.expectedFlocks, totalEggs: 0 } : d)) }
        : r)));
    renderWithProviders(<Dashboard />);
    await todayTotal();
    expect(screen.queryByText(/Yesterday by close/)).not.toBeInTheDocument();
  });
});

describe("Dashboard last 14 days (#654, INV-5)", () => {
  it("asks the production report for exactly the two 7-day windows ending yesterday", async () => {
    renderWithProviders(<Dashboard />);
    await todayTotal();
    expect(mockReport).toHaveBeenCalledTimes(2);
    // #916 — the third argument is the flock scope; All flocks (the default)
    // passes undefined, so the report stays farm-wide exactly as before.
    expect(mockReport).toHaveBeenCalledWith(daysBefore(today, 7), daysBefore(today, 1), undefined);
    expect(mockReport).toHaveBeenCalledWith(daysBefore(today, 14), daysBefore(today, 8), undefined);
  });

  it("draws the 14 report days oldest-first as bars sized off the peak, and the server's hen-day figures", async () => {
    renderWithProviders(<Dashboard />);
    // 4,396 eggs over 14 recorded days is 314.0 — the average is over the days
    // with an entry, which is a figure that only exists since #780.
    const strip = await screen.findByRole("group", {
      name: "Eggs per day, last 14 days. Peak 327, average 314.0. Every flock recorded every day.",
    });
    // 301..307 then 321..327, so the peak (327) is the 8th day and every other
    // bar is its exact share of it.
    expect(Array.from(strip.querySelectorAll(".day > i")).map((b) => (b as HTMLElement).style.height)).toEqual([
      "93.9%", "93.6%", "93.3%", "93%", "92.7%", "92.4%", "92%",
      "100%", "99.7%", "99.4%", "99.1%", "98.8%", "98.5%", "98.2%",
    ]);
    // Fourteen slots whatever the figures, and the divider on the 8th — the
    // first day of the recent window the hen-day figure below compares.
    const slots = Array.from(strip.querySelectorAll(".day"));
    expect(slots).toHaveLength(14);
    expect(slots.map((d) => d.classList.contains("day-week")).indexOf(true)).toBe(7);
    expect(screen.getByText("87.4%")).toBeInTheDocument();
    expect(screen.getByText("+2.3 pts")).toBeInTheDocument();
    expect(screen.getByText("Hen-day, last 7 days against the 7 before")).toBeInTheDocument();
  });

  it("keeps fourteen days in one flex row at 390px", async () => {
    vi.stubGlobal("innerWidth", 390);
    stubMatchMedia(false);
    try {
      renderWithProviders(<Dashboard />);
      const strip = await screen.findByRole("group", { name: /Eggs per day, last 14 days/ });
      expect(within(strip).getAllByRole("button")).toHaveLength(14);
      const styles = Array.from(document.styleSheets)
        .flatMap((sheet) => Array.from(sheet.cssRules, (rule) => rule.cssText)).join("");
      expect(styles).toMatch(/\.daystrip\s*\{[^}]*display:\s*flex/);
      expect(styles).not.toMatch(/\.daystrip\s*\{[^}]*display:\s*grid/);
    } finally {
      vi.unstubAllGlobals();
    }
  });

  // Days 4..6 of each window hold no entry at all. `entryCount` is the only
  // field that says so — before #780 these arrived as totalEggs 0, identical
  // to a day the farm recorded as having produced nothing.
  const withUnrecordedTail = () =>
    mockReport.mockImplementation((from, to) =>
      reportByWindow(today)(from, to).then((r) => ({
        ...r, days: r.days.map((d, i) => (i > 3 ? { ...d, totalEggs: 0, recordedFlocks: 0, missingFlocks: d.expectedFlocks } : d)),
      })));

  it("names the days that are not fully recorded, and averages over the rest", async () => {
    withUnrecordedTail();
    renderWithProviders(<Dashboard />);
    expect(await screen.findByRole("group", { name: /6 days are not fully recorded\.$/ })).toBeInTheDocument();
  });

  // #780 — the branch that exists so the panel never announces a peak it has no
  // evidence for. It shipped unrendered by any test: disabling it left the whole
  // suite green while the label said "Peak 0, average 0".
  it("announces no peak and no average when nothing in the window was recorded", async () => {
    mockReport.mockImplementation((from, to) =>
      reportByWindow(today)(from, to).then((r) => ({
        ...r, periodHenDayPct: null,
        days: r.days.map((d) => ({ ...d, totalEggs: 0, recordedFlocks: 0, missingFlocks: d.expectedFlocks })),
      })));
    renderWithProviders(<Dashboard />);
    const strip = await screen.findByRole("group", {
      name: "Eggs per day, last 14 days. No day in this window has an entry.",
    });
    // No bars at all, and the scale shows a dash rather than a fabricated 0.
    expect(strip.querySelectorAll(".day > i")).toHaveLength(0);
    expect(strip.querySelectorAll(".day")).toHaveLength(14);
    expect(screen.getByText("—", { selector: ".trend-peak" })).toBeInTheDocument();
    expect(screen.queryByText(/^Avg/)).not.toBeInTheDocument();
  });

  // Recorded, but never by every house — so there is still no complete day to
  // take a peak or an average from, and saying so is a different sentence.
  // #916 — no complete day exists, so this now falls back to the largest
  // partial day's total (327, the fixture's own peak) rather than announcing
  // "no peak or average" over a window whose caption shows a real number.
  it("scales to the partial peak, and says so, when some flocks recorded every day but never all of them", async () => {
    mockReport.mockImplementation((from, to) =>
      reportByWindow(today)(from, to).then((r) => ({
        ...r, days: r.days.map((d) => ({ ...d, recordedFlocks: 1, expectedFlocks: 3, missingFlocks: 2 })),
      })));
    renderWithProviders(<Dashboard />);
    expect(await screen.findByRole("group", {
      name: "Eggs per day, last 14 days. Peak 327, partial days only. No day was recorded by every flock, so there is no average.",
    })).toBeInTheDocument();
  });

  // A partly recorded day's total is a floor. It gets its own slot state and
  // its own sentence, and it must not drag the average down.
  it("marks a partly recorded day and keeps it out of the average", async () => {
    mockReport.mockImplementation((from, to) =>
      reportByWindow(today)(from, to).then((r) => ({
        ...r, days: r.days.map((d, i) => (i === 6 ? { ...d, recordedFlocks: 1, expectedFlocks: 3, missingFlocks: 2 } : d)),
      })));
    renderWithProviders(<Dashboard />);
    const strip = await screen.findByRole("group", { name: /Eggs per day, last 14 days/ });
    expect(strip.querySelectorAll(".day-partial")).toHaveLength(2); // day 6 of each window
    const names = screen.getAllByRole("button")
      .map((b) => b.getAttribute("aria-label"))
      .filter((n): n is string => n !== null && n.includes("of 3 flocks"));
    expect(names).toHaveLength(2);
    expect(names[0]).toMatch(/eggs, 1 of 3 flocks$/);
  });

  it("draws an empty slot for a day nobody recorded, and a stub for one that produced nothing", async () => {
    withUnrecordedTail();
    renderWithProviders(<Dashboard />);
    const strip = await screen.findByRole("group", { name: /Eggs per day, last 14 days/ });
    expect(strip.querySelectorAll(".day")).toHaveLength(14);
    // 8 recorded days draw a bar; the 6 with no entry draw nothing.
    expect(strip.querySelectorAll(".day > i")).toHaveLength(8);
  });

  // The pair this issue exists for, on one screen: a day that recorded zero
  // keeps a 2% stub, a day nobody recorded has no bar at all. Before #780 both
  // drew the same nothing.
  it("draws a stub for a recorded zero beside the empty slot of an unrecorded day", async () => {
    mockReport.mockImplementation((from, to) =>
      reportByWindow(today)(from, to).then((r) => ({
        ...r, days: r.days.map((d, i) => (i > 3 ? { ...d, totalEggs: 0, recordedFlocks: i > 5 ? 0 : 1, missingFlocks: i > 5 ? d.expectedFlocks : 0 } : d)),
      })));
    renderWithProviders(<Dashboard />);
    const strip = await screen.findByRole("group", { name: /Eggs per day, last 14 days/ });
    const heights = Array.from(strip.querySelectorAll(".day > i")).map((b) => (b as HTMLElement).style.height);
    // 8 days with real figures, plus days 4 and 5 of each window at the stub.
    expect(heights).toHaveLength(12);
    expect(heights.filter((h) => h === "2%")).toHaveLength(4);
    expect(strip.querySelectorAll(".day")).toHaveLength(14);
  });

  // The readout's plural selects on the EGG count, which is what the noun
  // beside it is. Selecting on the flock count rendered "1 eggs", and no test
  // used a partly recorded day holding exactly one egg, so the bug was
  // invisible to the whole suite.
  it("says '1 egg' on a partly recorded day that produced one", async () => {
    mockReport.mockImplementation((from, to) =>
      reportByWindow(today)(from, to).then((r) => ({
        ...r,
        days: r.days.map((d, i) => (i === 6 ? { ...d, totalEggs: 1, recordedFlocks: 1, expectedFlocks: 3, missingFlocks: 2 } : d)),
      })));
    renderWithProviders(<Dashboard />);
    await screen.findByRole("group", { name: /Eggs per day, last 14 days/ });
    const names = screen.getAllByRole("button")
      .map((b) => b.getAttribute("aria-label"))
      .filter((n): n is string => n !== null && n.includes("of 3 flocks"));
    expect(names).toHaveLength(2);
    expect(names[0]).toMatch(/ 1 egg, 1 of 3 flocks$/);
  });

  it("says 'no entry' for an unrecorded day rather than a count of zero", async () => {
    withUnrecordedTail();
    renderWithProviders(<Dashboard />);
    await screen.findByRole("group", { name: /Eggs per day, last 14 days/ });
    const names = screen.getAllByRole("button")
      .map((b) => b.getAttribute("aria-label"))
      .filter((n): n is string => n !== null && n.includes("–"));
    expect(names.filter((n) => n.endsWith("no entry"))).toHaveLength(6);
    expect(names.filter((n) => n.endsWith("0 eggs"))).toHaveLength(0);
  });

  it("shows a negative delta with the minus form, one decimal on both figures", async () => {
    mockReport.mockImplementation((from, to) =>
      reportByWindow(today)(from, to).then((r) => (r.periodHenDayPct === 87.4 ? report(80, r.days) : r)));
    renderWithProviders(<Dashboard />);
    expect(await screen.findByText("80.0%")).toBeInTheDocument();
    const delta = screen.getByText("−5.1 pts");
    expect(delta.className).toBe("trend-delta is-down");
  });

  it("renders — for a null hen-day figure, never 0, and keeps the delta neutral", async () => {
    mockReport.mockImplementation((from, to) =>
      reportByWindow(today)(from, to).then((r) => report(null, r.days)));
    renderWithProviders(<Dashboard />);
    const kpi = await screen.findByText("—", { selector: ".trend-fig" });
    expect(kpi).toBeInTheDocument();
    expect(screen.getByText("—", { selector: ".trend-delta" }).className).toBe("trend-delta");
  });
});

describe("Dashboard Lay rate flock scope (#916)", () => {
  it("defaults to All flocks and offers a searchable picker when more than one flock is accessible", async () => {
    renderWithProviders(<Dashboard />);
    await todayTotal();
    expect(mockReport).toHaveBeenLastCalledWith(daysBefore(today, 14), daysBefore(today, 8), undefined);
    const allButton = await screen.findByRole("button", { name: "All flocks" });
    expect(allButton).toHaveAttribute("aria-pressed", "true");
    expect(screen.getByLabelText("Flock")).toHaveValue("All flocks");
  });

  it("scopes the whole card to a picked flock, then back to All flocks — never touching the other panels", async () => {
    const user = userEvent.setup();
    renderWithProviders(<Dashboard />);
    await todayTotal();
    mockReport.mockClear();
    mockEntries.mockClear();

    await user.click(screen.getByLabelText("Flock"));
    // Scoped to the option role: "Flock f2" also names the Today row's own
    // link, ambiguous under a plain text query.
    await user.click(await screen.findByRole("option", { name: "Flock f2" }));

    await waitFor(() => expect(mockReport).toHaveBeenCalledWith(
      daysBefore(today, 7), daysBefore(today, 1), "f2"));
    expect(mockReport).toHaveBeenCalledWith(daysBefore(today, 14), daysBefore(today, 8), "f2");
    // A commit closes the picker (matches every other FlockPicker caller in
    // the app), so the closed-state label query resolves unambiguously again.
    expect(screen.getByLabelText("Flock")).toHaveValue("Flock f2");
    // Today's collection panel does not refetch on a Lay rate scope change.
    expect(mockEntries).not.toHaveBeenCalled();

    mockReport.mockClear();
    await user.click(screen.getByRole("button", { name: "All flocks" }));
    await waitFor(() => expect(mockReport).toHaveBeenCalledWith(
      daysBefore(today, 7), daysBefore(today, 1), undefined));
    expect(screen.getByLabelText("Flock")).toHaveValue("All flocks");
  });

  // #916 SELECTION.md — the only-one-flock view must report the SAME figures
  // as picking that flock out of a longer list: both derive `flockId` through
  // the identical code path (`soleFlockId` folds into the picker's own
  // scope), so this pins the observable half of that parity — the exact same
  // `getProductionReport` call, not a second "just show everything" branch.
  it("shows the sole accessible flock as plain text, with no picker, and scopes to it exactly as a manual pick would", async () => {
    mockFlocks.mockResolvedValue([flock("f1", "Active")]);
    renderWithProviders(<Dashboard />);
    const trendPanel = await panel("Last 14 days");
    expect(within(trendPanel).getByText("Flock f1")).toBeInTheDocument();
    expect(within(trendPanel).queryByRole("button", { name: "All flocks" })).not.toBeInTheDocument();
    expect(screen.queryByLabelText("Flock")).not.toBeInTheDocument();
    await waitFor(() => expect(mockReport).toHaveBeenCalledWith(
      daysBefore(today, 7), daysBefore(today, 1), "f1"));
    expect(mockReport).toHaveBeenCalledWith(daysBefore(today, 14), daysBefore(today, 8), "f1");
  });
});

describe("Dashboard stock bar (#654, INV-4)", () => {
  it("renders the grade bands with exact widths and a ledger row per grade, totalling the Stock screen's figure", async () => {
    renderWithProviders(<Dashboard />);
    const stock = await panel("Stock");
    await within(stock).findByText("1,560");
    const spans = Array.from(stock.querySelectorAll(".meter-stack > span")) as HTMLElement[];
    expect(spans.map((s) => [s.style.width, s.className])).toEqual([["79.5%", "grade-1"], ["20.5%", "grade-2"]]);
    // The ledger, not the band, is what names a grade and carries its share.
    const table = within(stock).getByRole("table", { name: "Stock by grade" });
    expect(within(table).getAllByRole("columnheader").map((cell) => cell.textContent)).toEqual(["Grade", "Count", "Share"]);
    const rows = within(table).getAllByRole("row").slice(1).map((row) => Array.from(row.children).map((cell) => cell.textContent));
    expect(rows).toEqual([["Grade A", "1,240", "79.5%"], ["Grade B", "320", "20.5%"]]);
    expect(within(stock).queryByText(/restricted/)).not.toBeInTheDocument();
  });

  it("shows the restricted line only when something is restricted", async () => {
    mockStock.mockResolvedValue([{ ...STOCK[0], restricted: 12 }, STOCK[1]]);
    renderWithProviders(<Dashboard />);
    expect(await screen.findByText("12 restricted")).toBeInTheDocument();
  });

  it("says 'egg available' — singular — when exactly one egg is in stock", async () => {
    mockStock.mockResolvedValue([{ eggGradeId: "g1", gradeName: "Grade A", available: 1, restricted: 0 }]);
    renderWithProviders(<Dashboard />);
    const stock = await panel("Stock");
    expect(stock.querySelector(".stock-total")?.textContent).toBe("1 egg available");
  });

  it("still renders a restricted-only stock (0 available, 4 restricted) — that is not the empty state", async () => {
    mockStock.mockResolvedValue([{ eggGradeId: "g1", gradeName: "Grade A", available: 0, restricted: 4 }]);
    renderWithProviders(<Dashboard />);
    const stock = await panel("Stock");
    expect(await within(stock).findByText("4 restricted")).toBeInTheDocument();
    expect(stock.querySelector(".stock-total")?.textContent).toBe("0 eggs available");
    expect(stock.querySelectorAll(".meter-stack > span")).toHaveLength(0);
    expect(within(stock).queryByRole("row", { name: /Grade A/ })).not.toBeInTheDocument();
    expect(within(stock).queryByRole("table", { name: "Stock by grade" })).not.toBeInTheDocument();
    expect(within(stock).queryByText("No stock yet — record and submit a daily entry.")).not.toBeInTheDocument();
  });
});

// One rejection at a time: the failed panel shows the panel error, every other
// panel still shows its real figures, and no zero appears where the failed
// figure would be (INV-1).
describe("Dashboard degrades one panel at a time (#654, INV-1)", () => {
  const boom = () => Promise.reject(new Error("down"));
  const asSales = { token: { sub: "u1", role: "Sales" } };
  const expectOthersIntact = async (except: "today" | "trend" | "stock" | "sales") => {
    if (except !== "today") expect(await todayTotal()).toBe("178");
    if (except !== "trend") expect(await screen.findByText("87.4%")).toBeInTheDocument();
    if (except !== "stock") expect(await screen.findByText("1,560")).toBeInTheDocument();
    if (except !== "sales") expect(await screen.findByText("No orders yet.")).toBeInTheDocument();
  };

  it("flocks failed → Today panel errors, others intact", async () => {
    mockFlocks.mockImplementation(boom);
    renderWithProviders(<Dashboard />, asSales);
    expect(within(await panel("Today")).getByText("Could not load.")).toBeInTheDocument();
    expect(screen.queryByText("Today so far")).not.toBeInTheDocument();
    await expectOthersIntact("today");
  });
  it("entries failed → Today panel errors, others intact", async () => {
    mockEntries.mockImplementation(boom);
    renderWithProviders(<Dashboard />, asSales);
    expect(within(await panel("Today")).getByText("Could not load.")).toBeInTheDocument();
    expect(screen.queryByText("Today so far")).not.toBeInTheDocument();
    await expectOthersIntact("today");
  });
  it("current-week report failed → trend panel errors, others intact", async () => {
    mockReport.mockImplementation((from, to) => (to === daysBefore(today, 1) ? boom() : reportByWindow(today)(from, to)));
    renderWithProviders(<Dashboard />, asSales);
    expect(within(await panel("Last 14 days")).getByText("Could not load.")).toBeInTheDocument();
    expect(screen.queryByRole("img")).not.toBeInTheDocument();
    await expectOthersIntact("trend");
  });
  it("previous-week report failed → trend panel errors, others intact", async () => {
    mockReport.mockImplementation((from, to) => (to === daysBefore(today, 8) ? boom() : reportByWindow(today)(from, to)));
    renderWithProviders(<Dashboard />, asSales);
    expect(within(await panel("Last 14 days")).getByText("Could not load.")).toBeInTheDocument();
    expect(screen.queryByText(/Hen-day/)).not.toBeInTheDocument();
    await expectOthersIntact("trend");
  });
  it("stock failed → Stock panel errors, others intact", async () => {
    mockStock.mockImplementation(boom);
    renderWithProviders(<Dashboard />, asSales);
    expect(within(await panel("Stock")).getByText("Could not load.")).toBeInTheDocument();
    expect(screen.queryByText(/eggs available/)).not.toBeInTheDocument();
    await expectOthersIntact("stock");
  });
  it("orders failed → Recent sales panel errors, others intact", async () => {
    mockOrders.mockImplementation(boom);
    renderWithProviders(<Dashboard />, asSales);
    expect(within(await panel("Recent sales")).getByText("Could not load.")).toBeInTheDocument();
    await expectOthersIntact("sales");
  });
  it("every issued fetch failed → the page-level message, not four panel errors", async () => {
    for (const m of [mockFlocks, mockEntries, mockStock, mockOrders]) m.mockImplementation(boom);
    mockReport.mockImplementation(boom);
    renderWithProviders(<Dashboard />, asSales);
    expect(await screen.findByText("Could not load dashboard. Is the API up?")).toBeInTheDocument();
    expect(screen.queryByText("Could not load.")).not.toBeInTheDocument();
  });

  it("every issued fetch failed for a ReadOnly user too — the inert sales placeholder does not count as a success", async () => {
    for (const m of [mockFlocks, mockEntries, mockStock]) m.mockImplementation(boom);
    mockReport.mockImplementation(boom);
    renderWithProviders(<Dashboard />, { token: { sub: "u1", role: "ReadOnly" } });
    expect(await screen.findByText("Could not load dashboard. Is the API up?")).toBeInTheDocument();
    expect(screen.queryByText("Could not load.")).not.toBeInTheDocument();
    expect(mockOrders).not.toHaveBeenCalled();
  });
});

// #127 — the customer/sales reads 403 for ReadOnly; the dashboard must not
// fetch them or render the sales panel.
describe("Dashboard sales panel role gate (#127)", () => {
  it("neither fetches nor shows sales for a ReadOnly user", async () => {
    renderWithProviders(<Dashboard />, { token: { sub: "u1", role: "ReadOnly" } });
    expect(await todayTotal()).toBe("178");
    expect(screen.queryByText("Recent sales")).not.toBeInTheDocument();
    expect(mockOrders).not.toHaveBeenCalled();
  });
  it("neither fetches nor shows sales for a Denied user", async () => {
    renderWithProviders(<Dashboard />, { token: { sub: "u1", role: "Denied" } });
    expect(await todayTotal()).toBe("178");
    expect(screen.queryByText("Recent sales")).not.toBeInTheDocument();
    expect(mockOrders).not.toHaveBeenCalled();
  });
  it("fetches and shows the sales panel for a non-ReadOnly user", async () => {
    renderWithProviders(<Dashboard />, { token: { sub: "u1", role: "Sales" } });
    expect(await screen.findByRole("heading", { name: "Recent sales" })).toBeInTheDocument();
    expect(mockOrders).toHaveBeenCalled();
  });
});

// #512 US4/US5 — rows carry their own customerName; the link goes to Sales
// filtered by the canonical customerId; never an id fragment.
describe("Dashboard recent sales rows (#512)", () => {
  it("renders a compact list row with the row-owned name linked to /sales?customerId=<id>", async () => {
    mockOrders.mockResolvedValue([order("o-1", "SO-3", "Filtered Farm")]);
    renderWithProviders(<Dashboard />, { token: { sub: "u1", role: "Sales" } });
    const row = await screen.findByRole("listitem", { name: /SO-3/ });
    expect(within(row).getByRole("link", { name: "Filtered Farm" })).toHaveAttribute("href", "/sales?customerId=c1");
    expect(within(row).getByText("Draft")).toBeInTheDocument();
    expect(within(row).getByText("$10.00")).toBeInTheDocument();
  });
  it("gives each row its own name and its own customer link, never the first row's", async () => {
    mockOrders.mockResolvedValue([
      { ...order("o-1", "SO-10", "First Farm") },
      { ...order("o-2", "SO-11", "Second Farm"), customerId: "c2" },
    ]);
    renderWithProviders(<Dashboard />, { token: { sub: "u1", role: "Sales" } });

    const first = await screen.findByRole("listitem", { name: /SO-10/ });
    const second = screen.getByRole("listitem", { name: /SO-11/ });
    expect(within(first).getByRole("link", { name: "First Farm" })).toHaveAttribute("href", "/sales?customerId=c1");
    expect(within(second).getByRole("link", { name: "Second Farm" })).toHaveAttribute("href", "/sales?customerId=c2");
    expect(within(second).queryByText("First Farm")).not.toBeInTheDocument();
  });
  it("shows the translated unavailable label for a null customerName — never an id fragment", async () => {
    mockOrders.mockResolvedValue([order("o-gone", "SO-1", null)]);
    renderWithProviders(<Dashboard />, { token: { sub: "u1", role: "Sales" } });
    const row = await screen.findByRole("listitem", { name: /SO-1/ });
    expect(within(row).getByText(i18n.t("dashboard:rowCustomerUnavailable"))).toBeInTheDocument();
    expect(within(row).queryByText("c1")).not.toBeInTheDocument();
  });
});

// #883 round 2, finding 5 — DIRECTION.md line 9's row action: a draft order
// gets a row action; a non-draft row has none.
//
// Codex CLI review round 2 (finding 3): the label read "Confirm order" —
// the Sales page's OWN control for the real, in-place confirm — while this
// row's link only opens the customer's WHOLE filtered order list (there is
// no per-order deep link yet), which can hold several draft/confirmed
// orders for the same customer. That is a real behavior/label mismatch, not
// just an extra click: the Today row's "Record"/"Continue" precedent still
// lands on the ONE exact form for that flock+date, so the label there never
// overclaims what one more step gets you. Reworded to "Review to confirm"
// — honest about being a navigation, not a completed action.
describe("Dashboard recent sales row action (#883 round 2, finding 5)", () => {
  it("shows a Review to confirm action on a draft row, linked through the customer filter", async () => {
    mockOrders.mockResolvedValue([order("o-1", "SO-3", "Filtered Farm")]); // status: "Draft"
    renderWithProviders(<Dashboard />, { token: { sub: "u1", role: "Sales" } });
    const row = await screen.findByRole("listitem", { name: /SO-3/ });
    expect(within(row).getByRole("link", { name: "Review to confirm" })).toHaveAttribute("href", "/sales?customerId=c1");
  });

  it("shows no action on a non-draft row", async () => {
    mockOrders.mockResolvedValue([{ ...order("o-2", "SO-4", "Second Farm"), status: "Confirmed" }]);
    renderWithProviders(<Dashboard />, { token: { sub: "u1", role: "Sales" } });
    const row = await screen.findByRole("listitem", { name: /SO-4/ });
    expect(within(row).queryByRole("link", { name: "Review to confirm" })).not.toBeInTheDocument();
  });
});

// The farm's day and locale win over the browser's (#123, #650): with time
// frozen at 23:30Z, a +14 farm is already on the next calendar day while any
// browser between UTC−12 and UTC+0:30 is not; a de-DE farm groups with "." and
// uses "," for the decimal.
describe("Dashboard follows the farm's day and locale", () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ["Date"] });
    vi.setSystemTime(new Date("2026-07-21T23:30:00Z"));
  });
  afterEach(() => vi.useRealTimers());

  it("asks for the farm's yesterday, links the tile to the farm's today, and formats in the farm locale", async () => {
    const farm = account({ locale: "de-DE", timeZoneId: "Pacific/Kiritimati" });
    const farmToday = "2026-07-22";
    expect(todayIso()).not.toBe(farmToday); // the browser is still on the 21st — the two days differ
    mockReport.mockImplementation(reportByWindow(farmToday));
    renderWithProviders(<Dashboard />, { farm });

    const f1 = await screen.findByRole("link", { name: "Flock f1: open today's entry" });
    expect(f1).toHaveAttribute("href", `/daily-entry?flockId=f1&date=${farmToday}`);
    // The tiles' own query must use the FARM's day too, not just the links and
    // the report windows: with the clock frozen at 23:30Z a +14 farm is already
    // on the 22nd while the browser is on the 21st, so a regression to
    // browser-local todayIso() shows yesterday's entries under today's date.
    expect(mockEntries).toHaveBeenCalledWith({ from: farmToday, to: farmToday, limit: 500 });
    expect(mockReport).toHaveBeenCalledWith("2026-07-15", "2026-07-21", undefined);
    expect(mockReport).toHaveBeenCalledWith("2026-07-08", "2026-07-14", undefined);
    expect(screen.getByText("1.560")).toBeInTheDocument();
    expect(screen.getByText("87,4%")).toBeInTheDocument();
    expect(screen.getByText("+2,3 pts")).toBeInTheDocument();
  });
});

// i18n wiring: swap catalog values at runtime so each marker only renders if
// the screen reads the catalog rather than a literal that happens to match.
describe("Dashboard i18n wiring (#654)", () => {
  it("reads the heading, the trend title and the today total from the catalog", async () => {
    await withOverride("dashboard", "morningHeading", "TITLE-MARKER", async () => {
      renderWithProviders(<Dashboard />);
      await panel("Today");
      expect(screen.getByRole("heading", { name: "Dashboard" })).toHaveTextContent("TITLE-MARKER");
    });
    await withOverride("dashboard", "trendPanelTitle", "TREND-MARKER", async () => {
      renderWithProviders(<Dashboard />);
      expect(await screen.findByText("TREND-MARKER")).toBeInTheDocument();
    });
    await withOverride("dashboard", "todaySoFarLabel", "TOTAL-MARKER", async () => {
      renderWithProviders(<Dashboard />);
      expect(await screen.findByText("TOTAL-MARKER")).toBeInTheDocument();
    });
  });
});

// #883 round 4, finding C — DIRECTION.md's status vocabulary is a dot plus a
// word, never a filled badge. StatusBadge itself stays untouched (its
// conversion is #831); the Dashboard renders its own dot locally.
describe("Dashboard status rendering (#864, dot not badge)", () => {
  it("renders Today and Recent sales status as a colour dot beside the word, not a filled badge", async () => {
    mockOrders.mockResolvedValue([order("o1", "SO-1", "Ramos Grocery")]); // status: "Draft"
    renderWithProviders(<Dashboard />);
    await screen.findByText("Today so far");

    // No filled badge survives on this screen once the conversion lands.
    expect(document.querySelectorAll(".badge").length).toBe(0);

    const recordedDot = within(todayRow("Flock f1")).getByText("Submitted").previousElementSibling;
    expect(recordedDot).toHaveAttribute("aria-hidden", "true");
    expect(recordedDot?.className).not.toMatch(/badge/);

    // f3 has no entry in the default fixture — the missing-house state.
    const missingDot = within(todayRow("Flock f3")).getByText("Not recorded").previousElementSibling;
    expect(missingDot).toHaveAttribute("aria-hidden", "true");

    const salesStatus = await screen.findByText("Draft");
    expect(salesStatus.previousElementSibling).toHaveAttribute("aria-hidden", "true");
  });
});

describe("Operations desk", () => {
  it("puts collection, stock, orders and lay rate in reading order", async () => {
    renderWithProviders(<Dashboard />);
    await panel("Today");
    expect(screen.getAllByRole("heading", { level: 3 }).map((heading) => heading.textContent))
      .toEqual(["Morning brief", "Morning collection", "Available stock", "Recent orders", "Lay rate"]);
  });

  it("lets a keyboard user focus each whole stock row", async () => {
    const user = userEvent.setup();
    renderWithProviders(<Dashboard />);
    const table = await screen.findByRole("table", { name: "Stock by grade" });
    const row = within(table).getByRole("row", { name: "Grade A 1,240 79.5%" });
    row.focus();
    expect(row).toHaveFocus();
    await user.tab();
    expect(within(table).getByRole("row", { name: "Grade B 320 20.5%" })).toHaveFocus();
  });

  it.each([
    ["Large", 1, "3,600 Large"],
    ["Large", 3, "3,600 Large +2"],
    ["", 1, "3,600"],
  ])("shows the first order quantity and grade (%s, %i lines)", async (eggGradeName, lines, expected) => {
    const sale = order("o1", "SO-GRADES", "Ramos Grocery");
    sale.items = Array.from({ length: lines }, (_, i) => ({
      id: `i${i}`, productId: `p${i}`, eggGradeId: `g${i}`, eggGradeName: i === 0 ? eggGradeName : ["Medium", "Small"][i - 1],
      unit: "Piece", baseUnitFactor: 1, quantity: i === 0 ? 3600 : i * 120, quantityBase: i === 0 ? 3600 : i * 120,
      unitPriceMinorUnits: 100, currencyCode: "USD", currencyMinorUnit: 2,
      listUnitPriceMinorUnits: null, listPriceBasis: "NoDefault",
    }));
    mockOrders.mockResolvedValue([sale]);
    renderWithProviders(<Dashboard />);
    const row = await screen.findByRole("listitem", { name: "SO-GRADES" });
    expect(within(row).getByText(expected)).toBeInTheDocument();
  });

  it("labels missing, partial and complete production days and navigates them with arrows", async () => {
    const user = userEvent.setup();
    mockReport.mockImplementation((from) => Promise.resolve(from === daysBefore(today, 14)
      ? report(80, [day("2026-07-01", 0, 0, 2), day("2026-07-02", 40, 1, 2), day("2026-07-03", 80, 2, 2)])
      : report(85, [])));
    renderWithProviders(<Dashboard />);
    const missing = await screen.findByRole("button", { name: "07/01/2026 – no entry" });
    missing.focus();
    await user.keyboard("{ArrowRight}");
    expect(screen.getByRole("button", { name: "07/02/2026 – 40 eggs, 1 of 2 flocks" })).toHaveFocus();
    await user.keyboard("{ArrowRight}");
    expect(screen.getByRole("button", { name: "07/03/2026 – 80 eggs" })).toHaveFocus();
  });
});
