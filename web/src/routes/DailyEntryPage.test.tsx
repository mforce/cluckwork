import { describe, it, expect, vi, beforeEach } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { render, screen, within, fireEvent, waitFor, act } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { DailyEntryPage } from "./DailyEntryPage";
import {
  listFlocks, listEggGrades, listEggUnitConversions, listDailyEntries, createFlock,
  listFeedUsage, listWaterUsage, recordDailyEntry, submitDailyEntry,
} from "../api/cluckwork";
import type { Flock, EggGrade, DailyEntry } from "../api/cluckwork";
import { todayIso } from "../lib/dates";
import { FarmContext } from "../farm/FarmContext";
import { account, farmState, NO_RECORD_HISTORY } from "../test/fixtures";
import { bindAccount, clearBoundAccount } from "../auth/tokenStore";
import i18n from "../i18n";

// #388 — the new-flock trigger/dialog are admin-gated, and this file has many
// direct <DailyEntryPage /> mounts with no <AuthProvider> ancestor, so useAuth
// is mocked directly rather than pulling in the real provider. `auth` is a
// single mutable object (vi.hoisted so the mock factory below can close over
// it) — beforeEach resets it to admin, and only the tests that care about
// gating flip it.
const auth = vi.hoisted(() => ({ isAdmin: true }));
vi.mock("../auth/useAuth", () => ({
  useAuth: () => ({ isAdmin: auth.isAdmin }),
}));

// Mock only the API seam it loads from. The MemoryRouter wrapper exists
// solely for the #446 summary strip's <Link>s.
vi.mock("../api/cluckwork", async (importOriginal) => {
  // Real module (formatMoney and friends stay genuine); only the network
  // seam is stubbed.
  const actual = await importOriginal<typeof import("../api/cluckwork")>();
  return {
    ...actual,
    listFlocks: vi.fn(),
    listFeedUsage: vi.fn(),
    listWaterUsage: vi.fn(),
    listEggGrades: vi.fn(),
    listEggUnitConversions: vi.fn(),
    listDailyEntries: vi.fn(),
    recordDailyEntry: vi.fn(),
    submitDailyEntry: vi.fn(),
    createFlock: vi.fn(),
  };
});

const mockListFlocks = vi.mocked(listFlocks);
const mockListEggGrades = vi.mocked(listEggGrades);
const mockListEggUnitConversions = vi.mocked(listEggUnitConversions);
const mockListDailyEntries = vi.mocked(listDailyEntries);
const mockListFeedUsage = vi.mocked(listFeedUsage);
const mockListWaterUsage = vi.mocked(listWaterUsage);
const mockCreateFlock = vi.mocked(createFlock);

const FLOCK: Flock = {
  ...NO_RECORD_HISTORY,
  id: "f1", farmId: "farm1", houseId: "h1", name: "Hen House 1", breed: "ISA",
  placementDate: "2026-01-01", initialCount: 100, currentBirds: 98, status: "Active",
};
const GRADES: EggGrade[] = [
  { ...NO_RECORD_HISTORY, id: "gr1", farmId: "farm1", name: "Grade A", gradeType: "Size", sortOrder: 1, isSaleable: true, dailyEntryKind: "Manual", active: true },
  { ...NO_RECORD_HISTORY, id: "gr2", farmId: "farm1", name: "Grade B", gradeType: "Size", sortOrder: 2, isSaleable: true, dailyEntryKind: "Manual", active: true },
];
// #396 — saleable AND active, so it passes every pre-existing filter. The only
// thing that keeps it out of the Grading pane is its kind.
const CRACKED: EggGrade = {
  ...NO_RECORD_HISTORY,
  id: "gr-cracked", farmId: "farm1", name: "Cracked", gradeType: "Quality",
  sortOrder: 3, isSaleable: true, dailyEntryKind: "Cracked", active: true,
};

beforeEach(() => {
  vi.clearAllMocks();
  auth.isAdmin = true;
  // #535 — boundAccountId is MODULE state read once at import and setup.ts never
  // resets it, so a bind leaks into later tests. Clear it so each test is unbound.
  clearBoundAccount();
  localStorage.clear();
  mockListFlocks.mockResolvedValue([FLOCK]);
  mockListEggGrades.mockResolvedValue(GRADES);
  // #444 — the seeded defaults every real account carries; Individual keeps
  // every pre-existing test's +1/-1 stepper arithmetic unchanged.
  mockListEggUnitConversions.mockResolvedValue([
    { id: "c1", unitCode: "Individual", eggsPerUnit: 1, active: true, version: 0 },
    { id: "c3", unitCode: "Tray", eggsPerUnit: 30, active: true, version: 0 },
  ]);
  mockListDailyEntries.mockResolvedValue([]); // no existing entry for the day
  // #446 — the day-support summary strip's own fetches; empty by default so
  // pre-existing tests see the zero state and nothing else changes.
  mockListFeedUsage.mockResolvedValue([]);
  mockListWaterUsage.mockResolvedValue([]);
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
// F134 Option A: the reconciliation is two readouts that sit with the fields
// they describe — sellable at the foot of the counts pane, and a chip counting
// DOWN to zero at the foot of the grading pane. Both are class-selected the way
// the footer already is; neither has a single unambiguous role or text.
function sellableReadout() {
  return document.querySelector(".entry-readout") as HTMLElement;
}
function remainingChip() {
  return document.querySelector(".entry-chip") as HTMLElement;
}

async function renderReady(isAdmin?: boolean) {
  if (isAdmin !== undefined) auth.isAdmin = isAdmin;
  render(<MemoryRouter><DailyEntryPage /></MemoryRouter>);
  await screen.findByLabelText("Grade A"); // mount load done, grades rendered
  // wait out the prefill fetch (it gates the save buttons via prefillPending)
  await waitFor(() => expect(saveDraftBtn()).toBeEnabled());
  expect(mockListDailyEntries).toHaveBeenCalled(); // prefill really ran
}

// #396 — Cracked and Dirty are fed by their own counters in Egg counts, so they
// must never appear as grade fields in the Grading pane. Their counter already
// produces a lot; a manual line naming one would produce a SECOND lot for the
// same grade on the same day. The server refuses it outright, so this is the
// affordance — the screen not offering a control whose only outcome is a
// rejection.
describe("DailyEntryPage grading pane excludes counter-fed grades", () => {
  it("offers no grade field for a saleable, active condition grade", async () => {
    mockListEggGrades.mockResolvedValue([...GRADES, CRACKED]);
    await renderReady();

    // Scoped to the GRADING pane. "Cracked" is a deliberately ambiguous label
    // on this screen: Egg counts has a Cracked *counter*, which must stay — an
    // unscoped query matches that and passes whatever the Grading pane does.
    // (The first version of this test did exactly that and had to be tightened.)
    const grading = screen.getByRole("heading", { name: /Grading/ })
      .closest("section") as HTMLElement;

    expect(within(grading).getByLabelText("Grade A")).toBeInTheDocument();
    expect(within(grading).getByLabelText("Grade B")).toBeInTheDocument();
    expect(within(grading).queryByLabelText("Cracked")).not.toBeInTheDocument();

    // ...while the counter it IS fed by stays where it belongs.
    const counts = screen.getByRole("heading", { name: /Egg counts/ })
      .closest("section") as HTMLElement;
    expect(within(counts).getByLabelText("Cracked")).toBeInTheDocument();
  });
});

describe("DailyEntryPage accuracy gating", () => {
  // #394: grading must reconcile EXACTLY to sellable before submit — "within
  // sellable" (the old gate) is no longer enough. This used to be the case
  // that shipped the bug: a partially-graded day submitted cleanly and
  // silently produced fewer egg lots than eggs actually collected.
  it("blocks submit for a partially graded day, even though graded is within sellable (#394)", async () => {
    await renderReady();
    setNum("Total eggs", 100);
    setNum("Cracked", 2);
    setNum("Dirty", 3);
    setNum("Discarded", 5); // losses 10 → sellable 90 (all three counted)
    setNum("Grade A", 60);
    setNum("Grade B", 25); // graded 85, short of 90 — no longer enough to submit

    expect(sellableReadout()).toHaveTextContent("90");
    // 90 sellable − 85 graded: the number the counter is working towards.
    expect(remainingChip()).toHaveTextContent("5 left to grade");
    expect(remainingChip()).not.toHaveClass("over");
    expect(remainingChip()).not.toHaveClass("done");
    expect(submitBtn()).toBeDisabled();
    expect(saveDraftBtn()).toBeEnabled(); // a draft stays flexible, even partly graded
  });

  // The bug exactly as reported (#394): no losses, no grading at all — the
  // day used to submit cleanly and generate zero egg lots for real production.
  it("blocks submit for a fully ungraded day, even with no losses at all (#394)", async () => {
    await renderReady();
    setNum("Total eggs", 200); // sellable 200, nothing graded

    expect(sellableReadout()).toHaveTextContent("200");
    expect(remainingChip()).toHaveTextContent("200 left to grade");
    expect(submitBtn()).toBeDisabled();
    expect(saveDraftBtn()).toBeEnabled();
  });

  it("blocks both saves and styles the message error when graded exceeds sellable", async () => {
    await renderReady();
    setNum("Total eggs", 100);
    setNum("Cracked", 2);
    setNum("Dirty", 3);
    setNum("Discarded", 5); // sellable 90
    setNum("Grade A", 95); // graded 95 > 90
    // #443 — typing now auto-raises the total to absorb the overshoot (95 +
    // 10 losses = 105), so pin the total back to its original 100 to force
    // the over state this test is about.
    setNum("Total eggs", 100);

    // Over-graded reads as an overage, not as a bigger number than the target.
    expect(remainingChip()).toHaveTextContent("5 over the sellable count");
    expect(remainingChip()).toHaveClass("over");
    expect(submitBtn()).toBeDisabled();
    // #443 — an over-graded draft used to be save-able (the backend would
    // reject it on its own); Save Draft is now blocked client-side too.
    expect(saveDraftBtn()).toBeDisabled();
  });

  it("allows submit at the exact boundary graded === sellable (#394: the gate is ===, nothing short or over)", async () => {
    await renderReady();
    setNum("Total eggs", 100);
    setNum("Cracked", 2);
    setNum("Dirty", 3);
    setNum("Discarded", 5); // sellable 90
    setNum("Grade A", 90); // graded 90 === 90

    expect(remainingChip()).toHaveTextContent("90 graded — the day adds up");
    expect(remainingChip()).toHaveClass("done");
    expect(remainingChip()).not.toHaveClass("over");
    expect(submitBtn()).toBeEnabled();
  });

  it("blocks both saves when cracked+dirty+discarded exceed total eggs", async () => {
    await renderReady();
    setNum("Total eggs", 10);
    setNum("Cracked", 2);
    setNum("Dirty", 3);
    setNum("Discarded", 6); // losses 11 > 10

    // The phone-only footer summary must not print a negative sum: `sellable`
    // is total - losses, so it goes below zero exactly here.
    const footSum = document.querySelector(".entry-foot-sum") as HTMLElement;
    expect(footSum).toHaveTextContent("Losses exceed the total — fix the counts");
    expect(footSum.textContent).not.toMatch(/-\d/);

    // In the counts pane, replacing the sellable figure — it is a counts
    // problem, so it belongs beside the counts and not under the grades.
    const msg = sellableReadout();
    expect(msg).toHaveTextContent("Cracked + dirty + discarded (11) exceed total eggs (10)");
    expect(msg).toHaveClass("error");
    expect(remainingChip()).toHaveTextContent("Fix the counts first");
    expect(submitBtn()).toBeDisabled();
    expect(saveDraftBtn()).toBeDisabled();
  });

  it("allows both saves at the exact boundary losses === total (zero graded)", async () => {
    await renderReady();
    setNum("Total eggs", 10);
    setNum("Cracked", 4);
    setNum("Dirty", 3);
    setNum("Discarded", 3); // losses 10 === total → sellable 0, graded 0

    expect(sellableReadout()).toHaveTextContent("0");
    expect(remainingChip()).toHaveClass("done");
    expect(submitBtn()).toBeEnabled();
    expect(saveDraftBtn()).toBeEnabled();
  });
});

describe("DailyEntryPage prefill gating", () => {
  it("blocks saving until the prefill lookup resolves", async () => {
    let resolvePrefill!: (entries: DailyEntry[]) => void;
    mockListDailyEntries.mockReturnValue(new Promise((r) => (resolvePrefill = r)));
    render(<MemoryRouter><DailyEntryPage /></MemoryRouter>);
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
      ...NO_RECORD_HISTORY,
      id: "de1", farmId: "farm1", houseId: "h1", flockId: "f1", date: todayIso(), status: "Submitted",
      totalEggs: 100, crackedEggs: 2, dirtyEggs: 3, discardedEggs: 5, mortalityCount: 1, crackedGradeId: null, dirtyGradeId: null,
      grades: [{ eggGradeId: "gr1", quantity: 60 }, { eggGradeId: "gr2", quantity: 25 }],
      version: 1, adjustReason: null, voidReason: null, lockedAtUtc: "2026-07-20T10:00:00Z", adjustedFrom: null,
    };
    mockListDailyEntries.mockResolvedValue([existing]);
    render(<MemoryRouter><DailyEntryPage /></MemoryRouter>);

    // the lock banner appears once the prefill applies the existing status
    expect(await screen.findByText(/already submitted/)).toBeInTheDocument();
    expect(screen.getByLabelText("Total eggs")).toBeDisabled();
    expect(submitBtn()).toBeDisabled();
    expect(saveDraftBtn()).toBeDisabled();
  });

  // Task 11 (#182): wiring the lock banner's status word through the `enums`
  // statusLabel helper is an INTENTIONAL harmonization, not text-preserving —
  // ManagerAdjusted used to render raw (lowercased to "manageradjusted", one
  // word, no visible boundary) and now reads "adjusted", matching the label
  // HistoryPage already shows for the same state (its own bespoke badge, see
  // en.ts's `enums` header comment).
  it("shows the harmonized 'adjusted' label, not the raw status, for a manager-adjusted day", async () => {
    const existing: DailyEntry = {
      ...NO_RECORD_HISTORY,
      id: "de1", farmId: "farm1", houseId: "h1", flockId: "f1", date: todayIso(), status: "ManagerAdjusted",
      totalEggs: 100, crackedEggs: 2, dirtyEggs: 3, discardedEggs: 5, mortalityCount: 1, crackedGradeId: null, dirtyGradeId: null,
      grades: [{ eggGradeId: "gr1", quantity: 60 }, { eggGradeId: "gr2", quantity: 25 }],
      version: 1, adjustReason: null, voidReason: null, lockedAtUtc: "2026-07-20T10:00:00Z", adjustedFrom: null,
    };
    mockListDailyEntries.mockResolvedValue([existing]);
    render(<MemoryRouter><DailyEntryPage /></MemoryRouter>);

    expect(await screen.findByText(/already adjusted/)).toBeInTheDocument();
    expect(screen.queryByText(/manageradjusted/i)).not.toBeInTheDocument();
    expect(submitBtn()).toBeDisabled();
  });
});

// F131: the "+ new flock" form used to unfold inline and push the whole entry
// grid down the page. It is a catalog create like any other, so it lives in a
// dialog now.
describe("DailyEntryPage new-flock dialog", () => {
  const dialog = () => screen.getByRole("dialog");
  const openNewFlock = () =>
    fireEvent.click(screen.getByRole("button", { name: "+ new flock" }));

  it("creates the flock with the full body and a key, then closes and selects it", async () => {
    mockCreateFlock.mockResolvedValue({ id: "f2" });
    const CREATED: Flock = { ...FLOCK, id: "f2", name: "Rhode Reds", breed: "Rhode Island Red" };
    await renderReady();
    // the refresh after the create must return the new flock so it can be selected
    mockListFlocks.mockResolvedValue([FLOCK, CREATED]);
    openNewFlock();

    // every field off its default (placed = today, birds = 100, name/breed = "")
    fireEvent.change(within(dialog()).getByLabelText("Name"), { target: { value: "Rhode Reds" } });
    fireEvent.change(within(dialog()).getByLabelText("Breed"), { target: { value: "Rhode Island Red" } });
    fireEvent.change(within(dialog()).getByLabelText("Placed"), { target: { value: "2026-05-10" } });
    fireEvent.change(within(dialog()).getByRole("spinbutton", { name: "Birds" }), { target: { value: "250" } });
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Create flock" }));
    });

    expect(mockCreateFlock.mock.calls[0][0]).toEqual({
      name: "Rhode Reds", breed: "Rhode Island Red",
      placementDate: "2026-05-10", initialCount: 250,
    });
    expect(mockCreateFlock.mock.calls[0][1]).toEqual(expect.any(String)); // idempotency key
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument(); // success dismisses it
    // the freshly created flock becomes the capture target
    expect(screen.getByLabelText("Flock")).toHaveValue("f2");
  });

  it("creates exactly one flock when double-submitted mid-flight (#236 — this form had no guard)", async () => {
    // Held promise: with no re-entry guard both clicks used to reach the API.
    let resolveCreate!: (v: { id: string }) => void;
    mockCreateFlock.mockReturnValue(new Promise((r) => (resolveCreate = r)));
    await renderReady();
    openNewFlock();

    fireEvent.change(within(dialog()).getByLabelText("Name"), { target: { value: "Rhode Reds" } });
    fireEvent.change(within(dialog()).getByLabelText("Breed"), { target: { value: "RIR" } });

    const form = within(dialog()).getByRole("button", { name: "Create flock" }).closest("form")!;
    await act(async () => {
      // Same-tick double submit on the form itself — a disabled button alone
      // cannot stop this; only the handler's own guard can.
      fireEvent.submit(form);
      fireEvent.submit(form);
    });
    expect(mockCreateFlock).toHaveBeenCalledTimes(1);

    await act(async () => resolveCreate({ id: "f2" }));
    expect(mockCreateFlock).toHaveBeenCalledTimes(1); // still exactly one after settle
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument(); // the one create succeeded
  });

  it("closes on Cancel without creating anything", async () => {
    await renderReady();
    openNewFlock();
    fireEvent.change(within(dialog()).getByLabelText("Name"), { target: { value: "Abandoned" } });

    fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));

    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(mockCreateFlock).not.toHaveBeenCalled();
  });
});

// #388 — flock creation is Owner/Manager administration: a scoped Worker
// cannot assign the flock it just created, so the trigger and the dialog it
// opens are both admin-gated here too (mirrors FlocksPage's own gate).
describe("DailyEntryPage new-flock admin gating (#388)", () => {
  it("hides the new-flock trigger from a worker", async () => {
    await renderReady(false);
    expect(screen.queryByRole("button", { name: "+ new flock" })).not.toBeInTheDocument();
  });

  it("shows the new-flock trigger to an admin", async () => {
    await renderReady(true);
    expect(screen.getByRole("button", { name: "+ new flock" })).toBeInTheDocument();
  });

  it("closes an open new-flock dialog the instant the role demotes away from admin", async () => {
    // Not renderReady(): this needs the render RESULT (for rerender) so the
    // SAME mounted tree is re-evaluated, the way a live role change would —
    // a fresh render() call would just mount closed from the start and prove
    // nothing about the gate reacting to a change (UsersPage.test.tsx's own
    // controlled-context demotion tests use the identical technique).
    auth.isAdmin = true;
    const view = render(<MemoryRouter><DailyEntryPage /></MemoryRouter>);
    await screen.findByLabelText("Grade A");
    await waitFor(() => expect(saveDraftBtn()).toBeEnabled());

    fireEvent.click(screen.getByRole("button", { name: "+ new flock" }));
    expect(screen.getByRole("dialog")).toBeInTheDocument();

    auth.isAdmin = false;
    view.rerender(<MemoryRouter><DailyEntryPage /></MemoryRouter>);

    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });
});

// F135: submit is one-way — it freezes the day and creates egg lots — so it
// asks first, in the app's own dialog rather than window.confirm.
describe("DailyEntryPage submit confirmation", () => {
  async function readyWithCounts() {
    await renderReady();
    fireEvent.change(screen.getByLabelText("Flock"), { target: { value: "f1" } });
    setNum("Total eggs", 10);
    // #394: submit needs grading to reconcile exactly — 10 sellable, 10 graded.
    setNum("Grade A", 10);
    await waitFor(() => expect(submitBtn()).toBeEnabled());
  }

  it("writes nothing until the question is answered", async () => {
    await readyWithCounts();

    await act(async () => { fireEvent.click(submitBtn()); });

    expect(screen.getByRole("dialog")).toHaveAccessibleName("Submit this day?");
    expect(vi.mocked(recordDailyEntry)).not.toHaveBeenCalled();
    expect(vi.mocked(submitDailyEntry)).not.toHaveBeenCalled();
  });

  it("saves and submits once confirmed", async () => {
    vi.mocked(recordDailyEntry).mockResolvedValue({ id: "e1" } as never);
    vi.mocked(submitDailyEntry).mockResolvedValue({ status: "Submitted" } as never);
    await readyWithCounts();

    await act(async () => { fireEvent.click(submitBtn()); });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Submit day" }));
    });

    expect(vi.mocked(recordDailyEntry)).toHaveBeenCalled();
    expect(vi.mocked(submitDailyEntry)).toHaveBeenCalledWith("e1", expect.any(String));
  });

  it("abandons the submit on Cancel, leaving the day untouched", async () => {
    await readyWithCounts();

    await act(async () => { fireEvent.click(submitBtn()); });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    });

    expect(screen.queryByRole("dialog")).toBeNull();
    expect(vi.mocked(recordDailyEntry)).not.toHaveBeenCalled();
    // The form is still live afterwards — dismissing is not a dead end.
    expect(submitBtn()).toBeEnabled();
  });

  it("saves a draft without asking — only submit is one-way", async () => {
    vi.mocked(recordDailyEntry).mockResolvedValue({ id: "e1" } as never);
    await readyWithCounts();

    await act(async () => { fireEvent.click(saveDraftBtn()); });

    expect(screen.queryByRole("dialog")).toBeNull();
    expect(vi.mocked(recordDailyEntry)).toHaveBeenCalled();
    expect(vi.mocked(submitDailyEntry)).not.toHaveBeenCalled();
  });
});

// F134: the screen is one undifferentiated pile of fields no more — three
// numbered steps in the order the work actually happens, with the
// reconciliation line and both saves pinned in a footer.
describe("DailyEntryPage structure", () => {
  it("labels the three steps without speaking the numerals twice", async () => {
    await renderReady();

    // Two steps, not three: choosing a flock and a date says WHICH day is being
    // recorded, it is not part of recording it. The drawn numeral is
    // aria-hidden and an off-screen "Step n of 2" carries the ordering, because
    // "of 2" is information document order cannot give.
    expect(screen.getByRole("heading", { name: "Step 1 of 2: Egg counts" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Step 2 of 2: Grading" })).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: /Flock/ })).toBeNull();
  });

  it("puts each readout with the fields it describes, and the saves in the footer", async () => {
    await renderReady();
    const panes = document.querySelectorAll(".entry-pane");
    const foot = document.querySelector(".entry-foot") as HTMLElement;

    // Sellable belongs to the counts that produce it; the remainder belongs to
    // the grades that consume it. Reading one while the other was a screen away
    // was the whole complaint.
    expect(panes[0].querySelector(".entry-readout")).not.toBeNull();
    expect(panes[1].querySelector(".entry-chip")).not.toBeNull();

    expect(within(foot).getByRole("button", { name: /Save draft/ })).toBeInTheDocument();
    expect(within(foot).getByRole("button", { name: /Save & submit/ })).toBeInTheDocument();
  });
});

describe("DailyEntryPage draft badge", () => {
  const draftFor = (date: string) => ({
    ...NO_RECORD_HISTORY,
    id: "de1", farmId: "farm1", houseId: "h1", flockId: "f1", date, status: "Draft",
    totalEggs: 40, crackedEggs: 1, dirtyEggs: 0, discardedEggs: 0, mortalityCount: 0, crackedGradeId: null, dirtyGradeId: null,
    grades: [], version: 1, adjustReason: null, voidReason: null,
    lockedAtUtc: null, adjustedFrom: null,
  } as DailyEntry);

  it("says so when the prefill lands on an existing draft", async () => {
    mockListDailyEntries.mockResolvedValue([draftFor(todayIso())]);
    render(<MemoryRouter><DailyEntryPage /></MemoryRouter>);
    await screen.findByLabelText("Grade A");

    // Without this the form looked identical whether it was a fresh day or an
    // edit of work already saved — only LOCKED days got any signal.
    expect(await screen.findByText("Editing draft")).toBeInTheDocument();
    expect(screen.getByLabelText("Total eggs")).toHaveValue(40);
  });

  it("stays absent on a fresh day", async () => {
    await renderReady();
    expect(screen.queryByText("Editing draft")).toBeNull();
  });

  it("appears as soon as a first draft is saved, not only after a reload", async () => {
    vi.mocked(recordDailyEntry).mockResolvedValue({ id: "e1" } as never);
    await renderReady();
    fireEvent.change(screen.getByLabelText("Flock"), { target: { value: "f1" } });
    setNum("Total eggs", 12);
    expect(screen.queryByText("Editing draft")).toBeNull();

    await act(async () => { fireEvent.click(saveDraftBtn()); });

    // Only the submit path tracked status before, so the day had saved work
    // that the badge did not admit to until something re-prefilled it.
    expect(screen.getByText("Editing draft")).toBeInTheDocument();
  });

  it("does not claim a draft while the prefill for a new day is still in flight", async () => {
    // Day one has a draft; switching to day two leaves existingStatus holding
    // the OLD day's value until the fetch lands.
    mockListDailyEntries.mockResolvedValueOnce([draftFor(todayIso())]);
    render(<MemoryRouter><DailyEntryPage /></MemoryRouter>);
    expect(await screen.findByText("Editing draft")).toBeInTheDocument();

    // Hold the next prefill open so the assertion lands INSIDE the pending
    // window. Waiting for it to settle would only ever exercise the
    // !prefillFailed half of the guard (review of PR #137).
    let settle!: (entries: DailyEntry[]) => void;
    mockListDailyEntries.mockReturnValueOnce(new Promise((r) => { settle = r; }));
    await act(async () => {
      fireEvent.change(screen.getByLabelText("Date"), { target: { value: "2026-07-01" } });
    });

    expect(screen.queryByText("Editing draft")).toBeNull(); // still in flight
    await act(async () => settle([]));
    expect(screen.queryByText("Editing draft")).toBeNull(); // and no draft found
  });

  it("does not claim a draft when the prefill for a new day fails", async () => {
    mockListDailyEntries.mockResolvedValueOnce([draftFor(todayIso())]);
    render(<MemoryRouter><DailyEntryPage /></MemoryRouter>);
    expect(await screen.findByText("Editing draft")).toBeInTheDocument();

    mockListDailyEntries.mockRejectedValueOnce(new Error("offline"));
    await act(async () => {
      fireEvent.change(screen.getByLabelText("Date"), { target: { value: "2026-07-01" } });
    });

    expect(await screen.findByText(/Could not check whether this day/)).toBeInTheDocument();
    expect(screen.queryByText("Editing draft")).toBeNull();
  });

  it("stays absent on a locked day, which has its own banner", async () => {
    mockListDailyEntries.mockResolvedValue([
      { ...draftFor(todayIso()), status: "Submitted" },
    ]);
    render(<MemoryRouter><DailyEntryPage /></MemoryRouter>);

    expect(await screen.findByText(/already submitted/)).toBeInTheDocument();
    expect(screen.queryByText("Editing draft")).toBeNull();
    // Every field, not just the first: the restructure moved all six
    // disabled={entryLocked} bindings, and checking one would let a slip on any
    // of the others through (review of PR #137).
    for (const label of ["Total eggs", "Cracked", "Dirty", "Discarded", "Mortality", "Grade A"]) {
      expect(screen.getByLabelText(label)).toBeDisabled();
    }
  });
});

describe("DailyEntryPage new-flock dialog errors", () => {
  it("shows a failed create inside the dialog instead of nowhere", async () => {
    mockCreateFlock.mockRejectedValue(new Error("Flock name already used."));
    await renderReady();
    fireEvent.click(screen.getByRole("button", { name: "+ new flock" }));

    const d = screen.getByRole("dialog");
    fireEvent.change(within(d).getByLabelText("Name"), { target: { value: "Dupe" } });
    fireEvent.change(within(d).getByLabelText("Breed"), { target: { value: "ISA" } });
    await act(async () => {
      fireEvent.click(within(d).getByRole("button", { name: "Create flock" }));
    });

    // The error render used to be gated on !showNewFlock while sitting INSIDE a
    // dialog that only exists when showNewFlock — so it could never appear and
    // the button looked inert (#131 regression, fixed in F134).
    const dlg = screen.getByRole("dialog");
    expect(within(dlg).getByText("Flock name already used.")).toBeInTheDocument();
    expect(dlg).toBeInTheDocument(); // stays open to retry
    // Exactly once: #479 gave this message its own "new-flock" dialog slot,
    // so the page-level footer (a separate slot) has nothing to double up on.
    expect(screen.getAllByText("Flock name already used.")).toHaveLength(1);
  });
});

// #479 — one slot per PLACE a message can appear. The dialog-own-failure
// guarantee above already existed pre-#479 (F134); these two are the ones the
// old shared slot could not make: an unrelated page failure must never land
// in the open dialog, and a page failure must survive both the dialog opening
// and a dialog write of its own failing.
describe("DailyEntryPage error placement (#479)", () => {
  const dialog = () => screen.getByRole("dialog");
  // DailyEntryPage reads window.location.search directly (not react-router's
  // own location), so a deep link is set the same way the app does — a plain
  // history navigation before render — and restored after.
  function withQuery(query: string) {
    const restore = window.location.href;
    window.history.pushState({}, "", `/daily-entry${query}`);
    return () => window.history.pushState({}, "", restore);
  }

  it("keeps an invalid deep-link failure off the open new-flock dialog", async () => {
    // No flockId, only a date: flockOk is false, so this never resolves to a
    // deep link but is still a URL the screen was asked to honor — the
    // screen's own mount-time failure, unrelated to creating a flock.
    const restore = withQuery("?date=2026-07-01");
    try {
      await renderReady();
      const message = i18n.t("dailyEntry:deepLinkUnavailable");
      expect(await screen.findByText(message)).toBeInTheDocument();

      fireEvent.click(screen.getByRole("button", { name: "+ new flock" }));

      expect(within(dialog()).queryByText(message)).not.toBeInTheDocument();
      expect(screen.getByText(message)).toBeInTheDocument();
    } finally {
      restore();
    }
  });

  it("keeps a page failure while the new-flock dialog opens and its own create fails", async () => {
    const restore = withQuery("?date=2026-07-01");
    try {
      mockCreateFlock.mockRejectedValue(new Error("Flock name already used."));
      await renderReady();
      const pageFailure = i18n.t("dailyEntry:deepLinkUnavailable");
      await screen.findByText(pageFailure);

      fireEvent.click(screen.getByRole("button", { name: "+ new flock" }));
      expect(screen.getByText(pageFailure)).toBeInTheDocument();

      fireEvent.change(within(dialog()).getByLabelText("Name"), { target: { value: "Dupe" } });
      fireEvent.change(within(dialog()).getByLabelText("Breed"), { target: { value: "ISA" } });
      await act(async () => {
        fireEvent.click(within(dialog()).getByRole("button", { name: "Create flock" }));
      });

      expect(within(dialog()).getByText("Flock name already used.")).toBeInTheDocument();
      expect(screen.getByText(pageFailure)).toBeInTheDocument();
    } finally {
      restore();
    }
  });
});

// F134: one gesture for the commonest last move — "and the rest are Large".
describe("DailyEntryPage assign the remainder", () => {
  // The chip's control ARMS the choice; the row buttons make it. Distinct
  // labels, because "put all in one grade" and "put all in Grade B" read the
  // same to anyone hearing them one at a time.
  const arm = () => screen.getByRole("button", { name: /Choose a grade for the remaining/ });
  const disarm = () => screen.getByRole("button", { name: "Cancel choosing a grade" });

  async function readyWithRemainder() {
    await renderReady();
    setNum("Total eggs", 100);
    setNum("Cracked", 10); // sellable 90
    setNum("Grade A", 30); // 60 left
  }

  it("hands the whole remainder to the grade that is picked", async () => {
    await readyWithRemainder();
    expect(remainingChip()).toHaveTextContent("60 left to grade");

    fireEvent.click(arm());
    // Named per grade: "+60" alone would be identical on every row.
    fireEvent.click(screen.getByRole("button", { name: "Put all 60 remaining in Grade B" }));

    // Grade B took all 60; the day now adds up exactly.
    expect(screen.getByLabelText("Grade B")).toHaveValue(60);
    expect(remainingChip()).toHaveClass("done");
  });

  // Our own payload type. Rows accept a drop only when they see it.
  const OURS = "application/x-cluckwork-remainder";
  const dt = (types: string[]) => ({ setData: () => {}, effectAllowed: "", types });

  it("takes the same drop from a drag, for anyone using a mouse", async () => {
    await readyWithRemainder();
    fireEvent.dragStart(arm(), { dataTransfer: dt([OURS]) });

    const rowB = screen.getByLabelText("Grade B").closest(".entry-row")!;
    fireEvent.dragOver(rowB, { dataTransfer: dt([OURS]) });
    fireEvent.drop(rowB, { dataTransfer: dt([OURS]) });

    expect(screen.getByLabelText("Grade B")).toHaveValue(60);
  });

  it("ignores anything dragged in from outside the app", async () => {
    await readyWithRemainder();
    fireEvent.dragStart(arm(), { dataTransfer: dt([OURS]) });

    // A file, a link, a selection from another window — the row used to accept
    // any of these and assign the whole remainder (codex review of PR #137).
    const rowB = screen.getByLabelText("Grade B").closest(".entry-row")!;
    fireEvent.drop(rowB, { dataTransfer: dt(["Files", "text/plain"]) });

    expect(screen.getByLabelText("Grade B")).toHaveValue(0);
    expect(remainingChip()).toHaveTextContent("60 left to grade");
  });

  it("can be armed and dismissed without changing anything", async () => {
    await readyWithRemainder();
    fireEvent.click(arm());
    expect(screen.getByRole("button", { name: "Put all 60 remaining in Grade B" })).toBeInTheDocument();

    fireEvent.click(disarm());
    expect(screen.queryByRole("button", { name: /Put all 60 remaining in/ })).toBeNull();
    expect(screen.getByLabelText("Grade B")).toHaveValue(0);
  });

  it("offers nothing to hand over once the day adds up", async () => {
    await renderReady();
    setNum("Total eggs", 100);
    setNum("Cracked", 10);
    setNum("Grade A", 90); // exactly sellable

    expect(remainingChip()).toHaveClass("done");
    expect(screen.queryByRole("button", { name: /Choose a grade/ })).toBeNull();
  });

  it("disarms itself if the remainder disappears while armed", async () => {
    await readyWithRemainder();
    fireEvent.click(arm());
    expect(screen.getByRole("button", { name: "Put all 60 remaining in Grade A" })).toBeInTheDocument();

    // Typing the rest in by hand leaves nothing to place.
    setNum("Grade B", 60);

    // Rows must not be left offering "+0".
    expect(screen.queryByRole("button", { name: /Put all \d+ remaining in/ })).toBeNull();
    expect(screen.queryByRole("button", { name: /Choose a grade/ })).toBeNull();
  });

  // The test above cannot tell WHICH mechanism disarmed: `fireEvent` wraps its
  // dispatch in act(), which flushes the passive effect before the assertion
  // runs, so it passes whether the render reads the derived `armed` flag or the
  // raw state. Dispatching raw skips that flush and sees the frame the user's
  // next click would land in — the one the bug lived in (#403 round 3).
  it("drops the row targets in the same render as the reconciliation", async () => {
    await readyWithRemainder();
    fireEvent.click(arm());

    const input = screen.getByLabelText("Grade B") as HTMLInputElement;
    // React tracks the value on the node, so assigning `.value` reads as a
    // no-op change; go through the prototype setter React patched.
    const setValue = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value")!.set!;
    setValue.call(input, "60"); // 30 + 60 === sellable 90
    input.dispatchEvent(new Event("input", { bubbles: true }));

    expect(screen.queryAllByRole("button", { name: /Put all \d+ remaining in/ })).toHaveLength(0);
    expect(document.querySelector(".entry-row.taking")).toBeNull();
  });

  // Same frame, different trigger, and the one the effect cannot cover at all:
  // the guard that would disqualify the gesture (`prefillPending`) is itself
  // set in an effect, so BOTH sides of the check were a render late. Changing
  // the day now disarms in the same event as the change.
  it("drops the row targets in the same render as a change of day", async () => {
    await readyWithRemainder();
    fireEvent.click(arm());
    expect(screen.getByRole("button", { name: "Put all 60 remaining in Grade A" })).toBeInTheDocument();

    const picker = screen.getByLabelText("Date") as HTMLInputElement;
    const setValue = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value")!.set!;
    setValue.call(picker, "2026-01-02");
    picker.dispatchEvent(new Event("input", { bubbles: true }));

    // No row may still offer the PREVIOUS day's remainder over the new one.
    expect(screen.queryAllByRole("button", { name: /Put all \d+ remaining in/ })).toHaveLength(0);
    expect(document.querySelector(".entry-row.taking")).toBeNull();
  });

  // The flock picker's own frame, not just the date's. Without it the flock
  // path's only defence was the source-text check, and a reviewer showed that
  // check can be walked around — one runtime test per picker, so neither rests
  // on the other (#403 round 5).
  it("drops the row targets in the same render as a change of flock", async () => {
    mockListFlocks.mockResolvedValue([FLOCK, { ...FLOCK, id: "f2", name: "Second Coop" }]);
    await readyWithRemainder();
    fireEvent.click(arm());
    expect(screen.getByRole("button", { name: "Put all 60 remaining in Grade A" })).toBeInTheDocument();

    const picker = screen.getByLabelText("Flock") as HTMLSelectElement;
    const setValue = Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, "value")!.set!;
    setValue.call(picker, "f2");
    picker.dispatchEvent(new Event("change", { bubbles: true }));

    expect(screen.queryAllByRole("button", { name: /Put all \d+ remaining in/ })).toHaveLength(0);
    expect(document.querySelector(".entry-row.taking")).toBeNull();
  });

  // The same switch reached through the other door. Creating a flock changes
  // the captured day too, and nothing prevents opening that dialog while armed
  // — so the fix has to live on every path that moves the target, not just the
  // two pickers (#403 round 4).
  it("drops the row targets when a newly created flock becomes the target", async () => {
    mockCreateFlock.mockResolvedValue({ id: "f2" });
    await readyWithRemainder();
    fireEvent.click(arm());
    expect(screen.getByRole("button", { name: "Put all 60 remaining in Grade A" })).toBeInTheDocument();

    mockListFlocks.mockResolvedValue([FLOCK, { ...FLOCK, id: "f2", name: "Rhode Reds" }]);
    fireEvent.click(screen.getByRole("button", { name: "+ new flock" }));
    const dlg = screen.getByRole("dialog");
    fireEvent.change(within(dlg).getByLabelText("Name"), { target: { value: "Rhode Reds" } });
    fireEvent.change(within(dlg).getByLabelText("Breed"), { target: { value: "RIR" } });
    await act(async () => {
      fireEvent.click(within(dlg).getByRole("button", { name: "Create flock" }));
    });

    expect(screen.getByLabelText("Flock")).toHaveValue("f2");
    expect(screen.queryAllByRole("button", { name: /Put all \d+ remaining in/ })).toHaveLength(0);
  });

  // Scope, because the test above cannot carry this on its own: the create
  // resolves through awaits, so settling it flushes the effects too, and the
  // assertion passes whether the disarm was synchronous or a render late
  // (measured — it survives removing `retarget` from that path). The frame is
  // observable for the two pickers and pinned there; for this path the
  // guarantee is structural instead, and this is what enforces it.
  it("routes every change of flock or date through the disarming helper", () => {
    const source = readFileSync(resolve(process.cwd(), "src/routes/DailyEntryPage.tsx"), "utf8")
      .replace(/\/\*[\s\S]*?\*\//g, "")
      .replace(/\/\/[^\n]*/g, "");

    // The setters are pinned to these names FIRST. Without this the check
    // greps for a literal that a rename makes disappear: a reviewer renamed
    // the state setter and called it raw, reintroducing the bug with this test
    // still green. Renaming is now the thing that fails, and the failure says
    // to update this list.
    expect(source, "flock setter name").toMatch(/const \[flockId, setFlockId\] = useState/);
    expect(source, "date setter name").toMatch(/const \[date, setDate\] = useState/);

    // Every call site, in or out of an effect. The mount path cannot be armed
    // yet, but it is routed anyway so the rule has no exceptions to remember.
    for (const setter of ["setFlockId", "setDate"]) {
      const calls = [...source.matchAll(new RegExp(`${setter}\\(`, "g"))];
      expect(calls.length, `${setter} call sites`).toBeGreaterThan(0);
      for (const call of calls) {
        const before = source.slice(Math.max(0, call.index - 40), call.index);
        expect(before, `${setter} at index ${call.index} must be inside retarget(...)`)
          .toMatch(/retarget\(\(\) =>\s*$/);
      }
    }

    // An alias would still let a raw call through under another name, so the
    // setters may not be re-bound at all. (A wrapper whose BODY calls the
    // setter is caught by the loop above; this closes the rename-at-source
    // spelling the loop cannot see.)
    for (const setter of ["setFlockId", "setDate"]) {
      expect(source, `${setter} must not be aliased`)
        .not.toMatch(new RegExp(`=\\s*${setter}\\s*[;,\\n]`));
    }
  });
});

// F134: the + refuses to build an over-graded day. Typing still can, because a
// draft is allowed to be over while it is being rearranged.
// #443 — grading may run ahead of the total (counted the grades before adding
// them up); the old ceiling that stopped a grade's + at the current total is
// gone, replaced by the total catching up to fit.
describe("DailyEntryPage grading sync (#443)", () => {
  it("no longer stops + at the point the day is fully graded — raises the total to fit instead", async () => {
    await renderReady();
    setNum("Total eggs", 10);
    setNum("Grade A", 9); // sellable 10, one left

    const plusA = screen.getByRole("button", { name: "Increase grade a" });
    fireEvent.pointerDown(plusA);
    fireEvent.pointerUp(plusA);
    expect(screen.getByLabelText("Grade A")).toHaveValue(10);
    expect(remainingChip()).toHaveClass("done");

    // Old behavior: this button would now be disabled. New behavior: it
    // keeps going, and the total rises to keep the day reconciled.
    expect(plusA).toBeEnabled();
    fireEvent.pointerDown(plusA);
    fireEvent.pointerUp(plusA);
    expect(screen.getByLabelText("Grade A")).toHaveValue(11);
    expect(screen.getByLabelText("Total eggs")).toHaveValue(11);
    expect(remainingChip()).toHaveClass("done");
  });

  // The two tests above only ever fire ONE onChange per interaction (a tap,
  // or a single programmatic change) — they cannot tell setGrade's
  // gradeQtyRef-based sum apart from one naively read off the `gradeQty`
  // closure, because NumberField's hold-to-repeat binds its WHOLE burst of
  // ticks to the single setGrade closure captured at press time (see
  // gradeQtyRef's own comment). Only a genuine multi-tick hold — several
  // repeat() ticks firing before this component ever re-renders — exercises
  // the reason the ref exists (codex review of #449 / adversarial review).
  it("accumulates correctly across a genuine multi-tick hold, not just the press-time snapshot", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      await renderReady();
      setNum("Total eggs", 10);
      setNum("Grade A", 9); // sellable 10, one left

      const plusA = screen.getByRole("button", { name: "Increase grade a" });
      await act(async () => { fireEvent.pointerDown(plusA); });
      // Same hold length and acceleration curve as NumberField.test.tsx's
      // "accelerates while held" case: press 1 + ticks 1-10 at +1 (10) +
      // ticks 11-16 at +5 (30) = 41 over 1300ms.
      await act(async () => { vi.advanceTimersByTime(1300); });
      await act(async () => { fireEvent.pointerUp(plusA); });

      expect(screen.getByLabelText("Grade A")).toHaveValue(9 + 41);
      // Every tick in the burst increased the sum, so the total tracked
      // every one of them, not just the value the burst started from — the
      // read-off-a-stale-closure regression would leave this frozen near 10.
      expect(screen.getByLabelText("Total eggs")).toHaveValue(9 + 41);
    } finally {
      vi.useRealTimers();
    }
  });

  it("does not touch the total when the grade still fits under it", async () => {
    await renderReady();
    setNum("Total eggs", 20);
    setNum("Grade A", 5); // sellable 20, fifteen left

    fireEvent.pointerDown(screen.getByRole("button", { name: "Increase grade a" }));
    fireEvent.pointerUp(screen.getByRole("button", { name: "Increase grade a" }));
    expect(screen.getByLabelText("Grade A")).toHaveValue(6);
    // Plenty of headroom left under 20 — nothing should have bumped it.
    expect(screen.getByLabelText("Total eggs")).toHaveValue(20);
  });

  it("reducing the total below an already-graded sum reaches 'over' and blocks both saves, without forcing the grade down", async () => {
    await renderReady();
    setNum("Total eggs", 10);
    setNum("Grade A", 10); // exactly reconciled
    expect(remainingChip()).toHaveClass("done");

    // The user lowers the total directly — this must never be fought by
    // pulling Grade A back down (bullet 2 of #443's requested change).
    setNum("Total eggs", 5);
    expect(screen.getByLabelText("Grade A")).toHaveValue(10);
    expect(remainingChip()).toHaveClass("over");
    expect(saveDraftBtn()).toBeDisabled();
    expect(submitBtn()).toBeDisabled();
  });

  // #444 — a farm that counts by the tray: every stepper on the screen bumps
  // by the farm default's eggsPerUnit instead of 1. Typing stays raw numbers.
  it("steps by the farm-default pack unit when one is set", async () => {
    render(
      <MemoryRouter>
        <FarmContext.Provider value={farmState({ farm: account({ defaultStepperUnit: "Tray" }) })}>
          <DailyEntryPage />
        </FarmContext.Provider>
      </MemoryRouter>);
    await screen.findByLabelText("Grade A");
    await waitFor(() => expect(saveDraftBtn()).toBeEnabled());

    fireEvent.pointerDown(screen.getByRole("button", { name: "Increase total eggs by 30" }));
    fireEvent.pointerUp(screen.getByRole("button", { name: "Increase total eggs by 30" }));
    expect(screen.getByLabelText("Total eggs")).toHaveValue(30);

    fireEvent.pointerDown(screen.getByRole("button", { name: "Increase grade a by 30" }));
    fireEvent.pointerUp(screen.getByRole("button", { name: "Increase grade a by 30" }));
    expect(screen.getByLabelText("Grade A")).toHaveValue(30);

    // The unit is visible at the point of touch AND named above the panes.
    expect(screen.getAllByText("+30").length).toBeGreaterThan(0);
    expect(screen.getByText(/Counting by Tray/)).toBeInTheDocument();

    // Mortality counts BIRDS, not eggs — a Tray farm must never record 30
    // deaths per tap (codex P1 review of #451). Un-suffixed name = step 1.
    const plusDeaths = screen.getByRole("button", { name: "Increase mortality" });
    fireEvent.pointerDown(plusDeaths);
    fireEvent.pointerUp(plusDeaths);
    expect(screen.getByLabelText("Mortality")).toHaveValue(1);
  });

  it("shows no unit caption when counting by ones", async () => {
    await renderReady(); // no farm context — resolves to Individual
    expect(screen.queryByText(/Counting by/)).toBeNull();
  });

  // codex review of #449: gating only on "still over" (rather than on this
  // EDIT increasing the graded sum) meant that fixing the over-graded day
  // above by walking Grade A back down with − ratcheted the total right back
  // up on every decrement, undoing the user's own step-1 correction.
  it("does not ratchet the total back up when correcting an over-graded day with −", async () => {
    await renderReady();
    setNum("Total eggs", 10);
    setNum("Grade A", 10); // exactly reconciled
    setNum("Total eggs", 5); // trimmed directly — now over

    const minusA = screen.getByRole("button", { name: "Decrease grade a" });
    fireEvent.pointerDown(minusA);
    fireEvent.pointerUp(minusA);

    expect(screen.getByLabelText("Grade A")).toHaveValue(9);
    expect(screen.getByLabelText("Total eggs")).toHaveValue(5);
  });
});

// #123 — the date field's ceiling comes from the FARM's clock. Since #35 the
// API judges "is this in the future?" against the farm's day, so a picker on
// the browser's day offers a date the save then refuses (device ahead of the
// farm) or hides a legitimate one (device behind).
describe("DailyEntryPage farm-local date", () => {
  const farmed = (timeZoneId: string) => render(
    <MemoryRouter>
      <FarmContext.Provider value={farmState({ farm: account({ timeZoneId }) })}>
        <DailyEntryPage />
      </FarmContext.Provider>
    </MemoryRouter>);

  it("opens on the farm's today and refuses to go past it", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    // One instant, three farm days: still the 15th in UTC and Los Angeles,
    // already the 16th in Tokyo.
    vi.setSystemTime(new Date("2026-07-15T23:30:00Z"));
    try {
      farmed("Asia/Tokyo");
      await screen.findByLabelText("Grade A");
      const date = screen.getByLabelText("Date");
      expect(date).toHaveAttribute("max", "2026-07-16");
      expect(date).toHaveValue("2026-07-16");
    } finally {
      vi.useRealTimers();
    }
  });

  it("gives a different farm a different ceiling at the same instant", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.setSystemTime(new Date("2026-07-15T23:30:00Z"));
    try {
      farmed("America/Los_Angeles");
      await screen.findByLabelText("Grade A");
      // The pair is the assertion: one clock, two farms, two days. A picker
      // reading the runner's zone cannot produce both.
      expect(screen.getByLabelText("Date")).toHaveAttribute("max", "2026-07-15");
    } finally {
      vi.useRealTimers();
    }
  });
});

// ---------------------------------------------------------------------------
// i18n wiring (#182, Task 11, batch B2)
// ---------------------------------------------------------------------------

// `dailyEntry` is English-only (not in TRANSLATED_NAMESPACES — see
// translations-status.ts), so under ANY UI language the rendered text falls
// back to this exact English string, same as a still-hardcoded literal would
// render — asserting it, even under a non-English locale, would prove nothing
// (CONTRIBUTING-i18n.md's fallback trap). Swap the catalog value at runtime
// instead, the same i18n.addResource technique the other batches use, so each
// marker only renders if the screen actually reads the catalog rather than a
// literal that happens to still match it.
describe("DailyEntryPage i18n wiring (#182, Task 11)", () => {
  function withOverride(ns: string, key: string, value: string, run: () => Promise<void> | void) {
    const original = i18n.getResource("en", ns, key) as string;
    i18n.addResource("en", ns, key, value);
    return Promise.resolve(run()).finally(() => {
      i18n.addResource("en", ns, key, original);
    });
  }

  it("reads the heading from the catalog, not a hardcoded literal", async () => {
    await withOverride("dailyEntry", "title", "TITLE-MARKER", async () => {
      await renderReady();
      expect(screen.getByRole("heading", { name: "TITLE-MARKER" })).toBeInTheDocument();
      expect(screen.queryByRole("heading", { name: "Daily entry" })).not.toBeInTheDocument();
    });
  });

  it("reads the save-draft button's label from the catalog, not a hardcoded literal", async () => {
    // Not renderReady(): it waits on the ORIGINAL "Save draft" name to confirm
    // the prefill settled, which the override below replaces.
    await withOverride("dailyEntry", "saveDraftButton", "SAVE-DRAFT-MARKER", async () => {
      render(<MemoryRouter><DailyEntryPage /></MemoryRouter>);
      await screen.findByLabelText("Grade A");
      await waitFor(() =>
        expect(screen.getByRole("button", { name: "SAVE-DRAFT-MARKER" })).toBeEnabled());
      expect(screen.queryByRole("button", { name: /Save draft/ })).not.toBeInTheDocument();
    });
  });

  // Proves the banner reads BOTH the catalog template AND the enum-labelled
  // (statusLabel), lowercased status — a hardcoded literal, or one that
  // interpolated the raw wire value, would show neither "LOCKED-MARKER" nor
  // "submitted" here.
  it("interpolates the enum-labelled status into the entry-locked banner from the catalog", async () => {
    const existing: DailyEntry = {
      ...NO_RECORD_HISTORY,
      id: "de1", farmId: "farm1", houseId: "h1", flockId: "f1", date: todayIso(), status: "Submitted",
      totalEggs: 100, crackedEggs: 2, dirtyEggs: 3, discardedEggs: 5, mortalityCount: 1, crackedGradeId: null, dirtyGradeId: null,
      grades: [], version: 1, adjustReason: null, voidReason: null, lockedAtUtc: "2026-07-20T10:00:00Z", adjustedFrom: null,
    };
    mockListDailyEntries.mockResolvedValue([existing]);
    await withOverride("dailyEntry", "entryLockedBanner", "LOCKED-MARKER {{status}} MARKER-END", async () => {
      render(<MemoryRouter><DailyEntryPage /></MemoryRouter>);
      expect(await screen.findByText("LOCKED-MARKER submitted MARKER-END")).toBeInTheDocument();
    });
  });

  it("reads the choose-a-grade aria-label from the catalog, interpolating the remaining count", async () => {
    await withOverride("dailyEntry", "armAriaLabel", "ARM-MARKER {{count}}", async () => {
      await renderReady();
      setNum("Total eggs", 100);
      setNum("Cracked", 10); // sellable 90, nothing graded yet → remaining 90
      expect(screen.getByRole("button", { name: "ARM-MARKER 90" })).toBeInTheDocument();
      expect(screen.queryByRole("button", { name: /Choose a grade for the remaining/ })).not.toBeInTheDocument();
    });
  });

  // The confirm dialog's copy is built with the imperative i18n.t() (onSave is
  // an event handler, not render — see CONTRIBUTING-i18n.md).
  it("reads the submit-confirmation dialog copy from the catalog", async () => {
    await withOverride("dailyEntry", "confirmSubmitTitle", "SUBMIT-TITLE-MARKER", () =>
      withOverride("dailyEntry", "confirmSubmitLabel", "SUBMIT-CONFIRM-MARKER", async () => {
        await renderReady();
        fireEvent.change(screen.getByLabelText("Flock"), { target: { value: "f1" } });
        setNum("Total eggs", 10);
        // #394: submit needs grading to reconcile exactly — 10 sellable, 10 graded.
        setNum("Grade A", 10);
        await waitFor(() => expect(submitBtn()).toBeEnabled());

        await act(async () => { fireEvent.click(submitBtn()); });

        expect(screen.getByRole("dialog")).toHaveAccessibleName("SUBMIT-TITLE-MARKER");
        expect(screen.getByRole("button", { name: "SUBMIT-CONFIRM-MARKER" })).toBeInTheDocument();
      }));
  });
});

// #446 — the day-support strip: what else was recorded for this flock+date
// (feed + water), queried by flock+date — NOT by DailyEntryId, so it works
// before the day's entry exists — with links to the pages that record them.
describe("DailyEntryPage feed/water day summary (#446)", () => {
  const feedRow = (over: object = {}) => ({
    id: "fu1", flockId: "f1", inventoryItemId: "i1", date: todayIso(),
    quantity: 18, unit: "kg", estimatedCostMinorUnits: 45_000,
    currencyCode: "USD", currencyMinorUnit: 2, note: null, dailyEntryId: null,
    ...over,
  });
  const waterRow = (over: object = {}) => ({
    id: "wu1", flockId: "f1", date: todayIso(), quantity: 250, unit: "L",
    source: "Well", meterStart: null, meterEnd: null, note: null, version: 1,
    dailyEntryId: null,
    ...over,
  });

  it("summarizes the day's feed and water for the selected flock+date, linking both pages", async () => {
    mockListFeedUsage.mockResolvedValue([feedRow(), feedRow({ id: "fu2", estimatedCostMinorUnits: 5_000 })]);
    mockListWaterUsage.mockResolvedValue([waterRow()]);
    await renderReady();

    expect(await screen.findByText("Feed: 2 records (est. 500.00 USD)")).toBeInTheDocument();
    expect(screen.getByText("Water: 1 record")).toBeInTheDocument();
    // Context rides along: the link lands filtered to this flock and day.
    expect(screen.getByRole("link", { name: /Feed: 2 records/ }))
      .toHaveAttribute("href", `/feed?flockId=f1&from=${todayIso()}&to=${todayIso()}`);
    expect(screen.getByRole("link", { name: /Water: 1 record/ }))
      .toHaveAttribute("href", `/water?flockId=f1&from=${todayIso()}&to=${todayIso()}`);
    // Queried by the page's own flock+date, never by the entry link.
    expect(mockListFeedUsage).toHaveBeenCalledWith(
      expect.objectContaining({ flockId: "f1", from: todayIso(), to: todayIso() }));
  });

  it("drops the cost — never a blended sum — when the day's feed rows span currencies", async () => {
    mockListFeedUsage.mockResolvedValue([
      feedRow(),
      feedRow({ id: "fu2", currencyCode: "JPY", currencyMinorUnit: 0, estimatedCostMinorUnits: 700 }),
    ]);
    await renderReady();
    expect(await screen.findByText("Feed: 2 records")).toBeInTheDocument();
    expect(screen.queryByText(/est\./)).not.toBeInTheDocument();
  });

  it("shows the zero state with both links when nothing was recorded yet", async () => {
    await renderReady();
    expect(await screen.findByText("Feed: 0 records")).toBeInTheDocument();
    expect(screen.getByText("Water: 0 records")).toBeInTheDocument();
  });

  it("pages past the API limit so the count and cost are true day totals", async () => {
    // The list endpoints page at 100; a single request would silently
    // underreport both figures for a heavy day. The strip must drain pages.
    const fullPage = Array.from({ length: 100 }, (_, i) =>
      feedRow({ id: `fu${i}`, estimatedCostMinorUnits: 100 }));
    mockListFeedUsage.mockResolvedValueOnce(fullPage);
    mockListFeedUsage.mockResolvedValueOnce([feedRow({ id: "fu-tail", estimatedCostMinorUnits: 500 })]);
    await renderReady();

    expect(await screen.findByText("Feed: 101 records (est. 105.00 USD)")).toBeInTheDocument();
    expect(mockListFeedUsage).toHaveBeenCalledWith(expect.objectContaining({ offset: 100 }));
  });

  it("a failed summary read hides the strip and leaves the entry form fully usable", async () => {
    mockListFeedUsage.mockRejectedValue(new Error("boom"));
    mockListWaterUsage.mockRejectedValue(new Error("boom"));
    await renderReady(); // renderReady itself asserts the save buttons enable
    expect(screen.queryByText(/^Feed: /)).not.toBeInTheDocument();
    setNum("Total eggs", 5);
    expect(screen.getByLabelText("Total eggs")).toHaveValue(5);
  });
});

// #535 — the remembered flock is namespaced by account so farm A's selection
// can't bleed into farm B on a shared device. The bare "cluckwork.lastFlockId"
// key is never read or written once an account is bound.
describe("DailyEntryPage account-scoped flock memory", () => {
  const GUID = "99999999-9999-9999-9999-999999999999";
  const NS_KEY = `cluckwork.lastFlockId:${GUID}`;
  const FLOCK2: Flock = { ...FLOCK, id: "f2", name: "Rhode Reds", breed: "RIR" };

  it("reads the namespaced key, never the bare key, and writes the namespaced key when a flock is chosen", async () => {
    bindAccount(GUID);
    mockListFlocks.mockResolvedValue([FLOCK, FLOCK2]);
    const getSpy = vi.spyOn(Storage.prototype, "getItem");

    await renderReady();
    // mount prefill read through accountScopedKey => the namespaced key
    expect(getSpy).toHaveBeenCalledWith(NS_KEY);
    // and the bare, pre-namespacing key was NEVER read
    const bareReads = getSpy.mock.calls.filter(([k]) => k === "cluckwork.lastFlockId");
    expect(bareReads).toHaveLength(0);

    // selecting a flock writes the NAMESPACED key (not the bare one)
    fireEvent.change(screen.getByLabelText("Flock"), { target: { value: "f2" } });
    await waitFor(() => expect(localStorage.getItem(NS_KEY)).toBe("f2"));
    expect(localStorage.getItem("cluckwork.lastFlockId")).toBeNull();
  });

  // #535 review round 1 — the case above pins the storage KEYS but never
  // asserts the remembered id actually SELECTS anything. Only with a genuine
  // selection asserted does deleting the remembered branch go red. FLOCK (f1)
  // is Active, so the default would prefer it — seed "f2" so this test cannot
  // pass via the default's first-active-flock fallback.
  it("actually selects the remembered flock", async () => {
    bindAccount(GUID);
    mockListFlocks.mockResolvedValue([FLOCK, FLOCK2]);
    localStorage.setItem(NS_KEY, "f2");

    await renderReady();

    expect(screen.getByLabelText("Flock")).toHaveValue("f2");
  });

  // #535 review round 1 — cross-account isolation was only INFERRED from the
  // key string, never exercised: no test bound one account, seeded a remembered
  // flock, then mounted under a DIFFERENT account and checked it fell back.
  // This catches "scoped, but scoped to the wrong or any account" (a prefix-scan
  // fallback in readAccountScoped reddens it), but because it asserts the DEFAULT
  // selection it stays green whenever the remembered read returns null for any
  // reason — including a broken prefill. The positive selection test above is
  // what covers selection; nobody should delete it believing this one covers it.
  it("does not leak farm A's remembered flock into farm B", async () => {
    const GUID_B = "88888888-8888-8888-8888-888888888888";
    bindAccount(GUID);
    localStorage.setItem(`cluckwork.lastFlockId:${GUID}`, "f2");
    mockListFlocks.mockResolvedValue([FLOCK, FLOCK2]);

    bindAccount(GUID_B);
    await renderReady();

    // farm B has no remembered flock of its own -> falls back to the first
    // active flock (f1).
    expect(screen.getByLabelText("Flock")).toHaveValue("f1");
  });
});
