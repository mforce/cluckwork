import { describe, it, expect, vi, beforeEach } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { render, screen, within, fireEvent, waitFor, act } from "@testing-library/react";
import { DailyEntryPage } from "./DailyEntryPage";
import {
  listFlocks, listEggGrades, listDailyEntries, createFlock, recordDailyEntry, submitDailyEntry,
} from "../api/cluckwork";
import type { Flock, EggGrade, DailyEntry } from "../api/cluckwork";
import { todayIso } from "../lib/dates";
import { FarmContext } from "../farm/FarmContext";
import { account, farmState } from "../test/fixtures";
import i18n from "../i18n";

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
const mockCreateFlock = vi.mocked(createFlock);

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

async function renderReady() {
  render(<DailyEntryPage />);
  await screen.findByLabelText("Grade A"); // mount load done, grades rendered
  // wait out the prefill fetch (it gates the save buttons via prefillPending)
  await waitFor(() => expect(saveDraftBtn()).toBeEnabled());
  expect(mockListDailyEntries).toHaveBeenCalled(); // prefill really ran
}

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

  it("blocks submit (but not draft) and styles the message error when graded exceeds sellable", async () => {
    await renderReady();
    setNum("Total eggs", 100);
    setNum("Cracked", 2);
    setNum("Dirty", 3);
    setNum("Discarded", 5); // sellable 90
    setNum("Grade A", 95); // graded 95 > 90

    // Over-graded reads as an overage, not as a bigger number than the target.
    expect(remainingChip()).toHaveTextContent("5 over the sellable count");
    expect(remainingChip()).toHaveClass("over");
    expect(submitBtn()).toBeDisabled();
    expect(saveDraftBtn()).toBeEnabled(); // an over-graded draft is allowed
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

  // Task 11 (#182): wiring the lock banner's status word through the `enums`
  // statusLabel helper is an INTENTIONAL harmonization, not text-preserving —
  // ManagerAdjusted used to render raw (lowercased to "manageradjusted", one
  // word, no visible boundary) and now reads "adjusted", matching the label
  // HistoryPage already shows for the same state (its own bespoke badge, see
  // en.ts's `enums` header comment).
  it("shows the harmonized 'adjusted' label, not the raw status, for a manager-adjusted day", async () => {
    const existing: DailyEntry = {
      id: "de1", farmId: "farm1", houseId: "h1", flockId: "f1", date: todayIso(), status: "ManagerAdjusted",
      totalEggs: 100, crackedEggs: 2, dirtyEggs: 3, discardedEggs: 5, mortalityCount: 1,
      grades: [{ eggGradeId: "gr1", quantity: 60 }, { eggGradeId: "gr2", quantity: 25 }],
      version: 1, adjustReason: null, voidReason: null, lockedAtUtc: "2026-07-20T10:00:00Z", adjustedFrom: null,
    };
    mockListDailyEntries.mockResolvedValue([existing]);
    render(<DailyEntryPage />);

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
    id: "de1", farmId: "farm1", houseId: "h1", flockId: "f1", date, status: "Draft",
    totalEggs: 40, crackedEggs: 1, dirtyEggs: 0, discardedEggs: 0, mortalityCount: 0,
    grades: [], version: 1, adjustReason: null, voidReason: null,
    lockedAtUtc: null, adjustedFrom: null,
  } as DailyEntry);

  it("says so when the prefill lands on an existing draft", async () => {
    mockListDailyEntries.mockResolvedValue([draftFor(todayIso())]);
    render(<DailyEntryPage />);
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
    render(<DailyEntryPage />);
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
    render(<DailyEntryPage />);
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
    render(<DailyEntryPage />);

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
    // Exactly once: the footer copy stays suppressed while the dialog is up, so
    // dropping that guard to "fix" the dialog would show the error twice.
    expect(screen.getAllByText("Flock name already used.")).toHaveLength(1);
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
describe("DailyEntryPage grading ceiling", () => {
  it("stops + at the point the day is fully graded", async () => {
    await renderReady();
    setNum("Total eggs", 10);
    setNum("Grade A", 9); // sellable 10, one left

    const plusA = screen.getByRole("button", { name: "Increase grade a" });
    expect(plusA).toBeEnabled();
    fireEvent.pointerDown(plusA);
    fireEvent.pointerUp(plusA);

    expect(screen.getByLabelText("Grade A")).toHaveValue(10);
    expect(remainingChip()).toHaveClass("done");
    // Nothing unallocated, so no grade can take more.
    expect(screen.getByRole("button", { name: "Increase grade a" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Increase grade b" })).toBeDisabled();
  });
});

// #123 — the date field's ceiling comes from the FARM's clock. Since #35 the
// API judges "is this in the future?" against the farm's day, so a picker on
// the browser's day offers a date the save then refuses (device ahead of the
// farm) or hides a legitimate one (device behind).
describe("DailyEntryPage farm-local date", () => {
  const farmed = (timeZoneId: string) => render(
    <FarmContext.Provider value={farmState({ farm: account({ timeZoneId }) })}>
      <DailyEntryPage />
    </FarmContext.Provider>);

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
      render(<DailyEntryPage />);
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
      id: "de1", farmId: "farm1", houseId: "h1", flockId: "f1", date: todayIso(), status: "Submitted",
      totalEggs: 100, crackedEggs: 2, dirtyEggs: 3, discardedEggs: 5, mortalityCount: 1,
      grades: [], version: 1, adjustReason: null, voidReason: null, lockedAtUtc: "2026-07-20T10:00:00Z", adjustedFrom: null,
    };
    mockListDailyEntries.mockResolvedValue([existing]);
    await withOverride("dailyEntry", "entryLockedBanner", "LOCKED-MARKER {{status}} MARKER-END", async () => {
      render(<DailyEntryPage />);
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
