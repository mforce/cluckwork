import { describe, it, expect, vi, beforeEach } from "vitest";
import { screen, within, fireEvent, act } from "@testing-library/react";
import { HistoryPage } from "./HistoryPage";
import { renderWithProviders } from "../test/renderWithProviders";
import {
  adjustDailyEntry, getDailyEntry, listDailyEntries, listEggGrades, listFlocks, voidDailyEntry,
} from "../api/cluckwork";
import type { DailyEntry, EggGrade, Flock } from "../api/cluckwork";
import { ApiError } from "../api/client";
import i18n from "../i18n";

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
const mockGetDailyEntry = vi.mocked(getDailyEntry);

const FLOCK: Flock = {
  id: "f1", farmId: "farm1", houseId: "h1", name: "Hen House 1", breed: "ISA",
  placementDate: "2026-01-01", initialCount: 100, currentBirds: 98, status: "Active",
};
const ARCHIVED_FLOCK: Flock = { ...FLOCK, id: "f2", name: "Old Coop", status: "Archived" };
const GRADE_A: EggGrade = { id: "gr1", farmId: "farm1", name: "Grade A", gradeType: "Size", sortOrder: 1, isSaleable: true, dailyEntryKind: "Manual", active: true };
const GRADE_B: EggGrade = { id: "gr2", farmId: "farm1", name: "Grade B", gradeType: "Size", sortOrder: 2, isSaleable: true, dailyEntryKind: "Manual", active: true };

// sellable = 100 − 2 − 3 − 5 = 90; two graded lines summing to 60 (within).
const SUBMITTED: DailyEntry = {
  id: "de1", farmId: "farm1", houseId: "h1", flockId: "f1", date: "2026-07-19", status: "Submitted",
  totalEggs: 100, crackedEggs: 2, dirtyEggs: 3, discardedEggs: 5, mortalityCount: 1, crackedGradeId: null, dirtyGradeId: null,
  grades: [{ eggGradeId: "gr1", quantity: 40 }, { eggGradeId: "gr2", quantity: 20 }],
  version: 1, adjustReason: null, voidReason: null, lockedAtUtc: null, adjustedFrom: null,
};
const DRAFT: DailyEntry = { ...SUBMITTED, id: "de2", date: "2026-07-18", status: "Draft", grades: [] };
const DRAFT_ARCHIVED: DailyEntry = { ...DRAFT, id: "de3", flockId: "f2" };
// The three statusCell states that carry their own bespoke badge + tooltip
// (#182, Task 27) — a DISTINCT vocabulary from the shared enums:status family.
const VOIDED: DailyEntry = { ...SUBMITTED, id: "de4", status: "Voided", voidReason: "spoiled" };
const MANAGER_ADJUSTED: DailyEntry = { ...SUBMITTED, id: "de5", status: "ManagerAdjusted", adjustReason: "recount" };
const LOCKED: DailyEntry = { ...SUBMITTED, id: "de6", status: "Locked", lockedAtUtc: "2026-07-19T08:00:00Z" };

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

// #396 — the Condition column answers "how many of this day's cracked/dirty
// eggs became stock", read from the ENTRY's own snapshot. It must never be
// re-derived from the current grade catalog: a farm that switches Cracked off
// today would otherwise see past days lose stock they already sold.
// Columns: date, flock, status, total, losses, CONDITION, mortality, graded,
// actions. Indexed rather than matched by text because "0" and the em dash both
// occur in sibling cells, so a text query can pass against the wrong column.
const conditionCell = (row: HTMLElement) => within(row).getAllByRole("cell")[5];

describe("HistoryPage condition column", () => {
  it("counts only the conditions this entry resolved to a grade", async () => {
    // cracked 2 resolved (a grade id), dirty 3 did NOT (null) — so 2, not 5.
    // Every number here is distinct, so a column wired to the wrong field, or
    // one that sums the raw counters, produces a different value.
    mockListDailyEntries.mockResolvedValue([
      { ...SUBMITTED, crackedGradeId: "gr1", dirtyGradeId: null },
    ]);
    renderWithProviders(<HistoryPage />, { token: ADMIN });

    const row = await screen.findByRole("row", { name: /2026-07-19/ });
    within(row).getByText("2/3/5"); // Losses still shows all three counters
    // By CELL, not by text: "2" also appears inside the losses cell, so a text
    // match would pass against a column that renders the wrong number.
    expect(conditionCell(row)).toHaveTextContent("2");
  });

  it("shows an em dash for a draft rather than 0", async () => {
    // A draft has resolved nothing yet. 0 would state "these were a loss",
    // which is a different fact and not yet true.
    mockListDailyEntries.mockResolvedValue([DRAFT]);
    renderWithProviders(<HistoryPage />, { token: ADMIN });

    const row = await screen.findByRole("row", { name: /2026-07-18/ });
    expect(conditionCell(row)).toHaveTextContent("—");
  });

  it("shows 0 for an official entry whose conditions were losses", async () => {
    mockListDailyEntries.mockResolvedValue([SUBMITTED]); // both snapshots null
    renderWithProviders(<HistoryPage />, { token: ADMIN });

    const row = await screen.findByRole("row", { name: /2026-07-19/ });
    expect(conditionCell(row)).toHaveTextContent("0");
  });
});

// #396 — the adjust dialog is the Daily entry form, so the same rule holds:
// a counter-fed grade is never offered for adding. Excluded from the CATALOG
// half only — an existing line stays correctable whatever it names.
describe("HistoryPage adjust panel excludes counter-fed grades", () => {
  const CRACKED: EggGrade = {
    id: "gr-cracked", farmId: "farm1", name: "Cracked", gradeType: "Quality",
    sortOrder: 3, isSaleable: true, dailyEntryKind: "Cracked", active: true,
  };

  it("offers no grade field for a saleable, active condition grade", async () => {
    mockListEggGrades.mockResolvedValue([GRADE_A, GRADE_B, CRACKED]);
    mockListDailyEntries.mockResolvedValue([SUBMITTED]);
    await openAdjustPanel();

    const dialog = screen.getByRole("dialog");
    expect(within(dialog).getByLabelText("Grade A")).toBeInTheDocument();
    // Scoped to the dialog: the row behind it also renders the word, and the
    // dialog's own Egg-counts half has a Cracked COUNTER that must stay.
    expect(within(dialog).queryByLabelText("Cracked")).toBeInTheDocument();
    // ...so assert on the grade field specifically: the counter is the only
    // "Cracked" control the dialog may show, and it is a count input, not a
    // grade line. A grade field would make TWO.
    expect(within(dialog).getAllByLabelText("Cracked")).toHaveLength(1);
  });
});

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

// #394 — an adjustment has no draft state of its own: Save stays disabled
// until grading reconciles EXACTLY to sellable, the same rule Daily Entry's
// submit uses. Renamed from "sellable guard" — the old guard only blocked
// going OVER; this one blocks under, over, and — the interesting new case —
// the entry's OWN stored grades not reconciling the moment the dialog opens.
describe("HistoryPage adjust — reconciliation guard", () => {
  // SUBMITTED's own fixture grades (40 + 20 = 60) are short of its sellable
  // (90) — a state #394 makes legal to have STORED (a pre-existing entry
  // submitted before this rule, or one an earlier codex-flagged guard already
  // let through) but not to SAVE without fixing. This is the base state every
  // other test below edits away from.
  it("opens already disabled when the entry's own stored grades don't reconcile", async () => {
    mockListDailyEntries.mockResolvedValue([SUBMITTED]);
    await openAdjustPanel();
    // Filled so this isolates the GRADES gate — otherwise the still-empty
    // reason field alone would explain the disabled state just as well.
    fireEvent.change(screen.getByLabelText(/Reason/), { target: { value: "recount" } });

    expect(screen.getByRole("button", { name: "Save adjustment" })).toBeDisabled();
  });

  it("stays disabled when the graded lines are short of sellable", async () => {
    mockListDailyEntries.mockResolvedValue([SUBMITTED]);
    await openAdjustPanel();

    // 46 + 40 = 86, short of sellable 90.
    fireEvent.change(screen.getByRole("spinbutton", { name: "Grade A" }), { target: { value: "46" } });
    fireEvent.change(screen.getByRole("spinbutton", { name: "Grade B" }), { target: { value: "40" } });
    fireEvent.change(screen.getByLabelText(/Reason/), { target: { value: "recount" } });

    expect(screen.getByRole("button", { name: "Save adjustment" })).toBeDisabled();
  });

  it("stays disabled when the graded lines SUM past sellable (neither line alone over)", async () => {
    mockListDailyEntries.mockResolvedValue([SUBMITTED]);
    await openAdjustPanel();

    // 46 + 45 = 91 > sellable 90, yet neither line individually exceeds 90 —
    // so this only fails if the guard actually SUMS the lines. #443 made
    // typing an overshoot auto-raise the total to absorb it (91 + 10 losses
    // = 101), so pin the total back to its original 100 afterward to force
    // the genuinely-over state this test is about.
    fireEvent.change(screen.getByRole("spinbutton", { name: "Grade A" }), { target: { value: "46" } });
    fireEvent.change(screen.getByRole("spinbutton", { name: "Grade B" }), { target: { value: "45" } });
    fireEvent.change(screen.getByRole("spinbutton", { name: "Total eggs" }), { target: { value: "100" } });
    fireEvent.change(screen.getByLabelText(/Reason/), { target: { value: "recount" } });

    expect(screen.getByRole("button", { name: "Save adjustment" })).toBeDisabled();
    expect(mockAdjustDailyEntry).not.toHaveBeenCalled();
  });

  // The button being disabled is the primary gate; onAdjustSubmit's own check
  // is defense in depth and would otherwise be dead code — bypass the
  // disabled button by dispatching submit on the form directly (same idiom
  // DailyEntryPage.test.tsx uses for its own-submit-guard coverage) to prove
  // that check still refuses on its own.
  it("refuses via the handler's own guard even if the disabled button is bypassed", async () => {
    mockListDailyEntries.mockResolvedValue([SUBMITTED]);
    await openAdjustPanel();

    fireEvent.change(screen.getByRole("spinbutton", { name: "Grade A" }), { target: { value: "46" } });
    fireEvent.change(screen.getByRole("spinbutton", { name: "Grade B" }), { target: { value: "45" } });
    // #443 — pin the total back down; see the sibling test above for why.
    fireEvent.change(screen.getByRole("spinbutton", { name: "Total eggs" }), { target: { value: "100" } });
    fireEvent.change(screen.getByLabelText(/Reason/), { target: { value: "recount" } });
    const saveButton = screen.getByRole("button", { name: "Save adjustment" });
    expect(saveButton).toBeDisabled();

    await act(async () => {
      fireEvent.submit(saveButton.closest("form")!);
    });

    expect(await screen.findByRole("alert")).toHaveTextContent(/must equal total eggs/);
    expect(mockAdjustDailyEntry).not.toHaveBeenCalled();
  });

  it("enables Save and submits the corrected lines at the exact boundary sum === sellable", async () => {
    mockListDailyEntries.mockResolvedValue([SUBMITTED]);
    mockAdjustDailyEntry.mockResolvedValue({ id: "de1", status: "ManagerAdjusted", version: 2 });
    await openAdjustPanel();

    fireEvent.change(screen.getByRole("spinbutton", { name: "Grade A" }), { target: { value: "45" } });
    fireEvent.change(screen.getByRole("spinbutton", { name: "Grade B" }), { target: { value: "45" } }); // 90 === sellable
    fireEvent.change(screen.getByLabelText(/Reason/), { target: { value: "recount" } });
    expect(screen.getByRole("button", { name: "Save adjustment" })).toBeEnabled();

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Save adjustment" }));
    });

    // By text, then role-checked: BusyButton (#236) mounts an always-present
    // status live region per row button, so role alone is ambiguous now.
    const done = await screen.findByText(/Entry adjusted/);
    expect(done).toHaveAttribute("role", "status");
    const [id, body] = mockAdjustDailyEntry.mock.calls[0];
    expect(id).toBe("de1");
    expect(body).toMatchObject({
      // No crackedGradeId/dirtyGradeId here on purpose: an adjustment never
      // re-resolves the condition grades, so the request does not carry them
      // (#396). They live on the RESPONSE only.
      version: 1, totalEggs: 100, crackedEggs: 2, dirtyEggs: 3, discardedEggs: 5, mortalityCount: 1,
      reason: "recount", grades: [{ eggGradeId: "gr1", quantity: 45 }, { eggGradeId: "gr2", quantity: 45 }],
    });
  });
});

// The dialog mirrors the Daily entry screen's two-step layout: the counts pane
// produces the sellable figure, the grading pane has to hit it, and the chip
// between them says how far off it is. Everything below asserts the DIALOG's
// own readouts — the same numbers Save is gated on, so a drift between what it
// shows and what it allows would fail here.
describe("HistoryPage adjust — mirrored daily-entry layout", () => {
  const dialog = () => screen.getByRole("dialog");
  // Class-selected, exactly as DailyEntryPage.test.tsx selects the same two
  // readouts: neither has an unambiguous role here either — every BusyButton
  // renders its own sr-only role="status" for the "Working…" announcement, so
  // the chip's live region is one of several.
  const chip = () => dialog().querySelector(".entry-chip") as HTMLElement;
  const sellableReadout = () => dialog().querySelector(".entry-readout") as HTMLElement;

  it("shows both steps and the sellable figure the grading pane has to hit", async () => {
    mockListDailyEntries.mockResolvedValue([SUBMITTED]);
    await openAdjustPanel();

    // Both step headings, in order — this is the layout the correction shares
    // with capture, not a flat list of fields. The dialog's own title is an h3
    // too, so the steps are the two that follow it.
    const headings = within(dialog()).getAllByRole("heading", { level: 3 });
    expect(headings.slice(1).map((h) => h.textContent)).toEqual([
      expect.stringContaining("Egg counts"),
      expect.stringContaining("Grading"),
    ]);

    // 100 − 2 − 3 − 5 = 90, shown as a value beside its own formula.
    expect(sellableReadout()).toHaveTextContent("Sellable");
    expect(sellableReadout()).toHaveTextContent("100 − 2 − 3 − 5");
    expect(within(sellableReadout()).getByText("90")).toBeInTheDocument();
  });

  it("counts the remainder down as grades are typed, then confirms the day adds up", async () => {
    mockListDailyEntries.mockResolvedValue([SUBMITTED]);
    await openAdjustPanel();

    // Fixture grades are 40 + 20 = 60 against sellable 90.
    expect(chip()).toHaveTextContent("30");
    expect(chip()).toHaveTextContent("left to grade");

    fireEvent.change(screen.getByRole("spinbutton", { name: "Grade A" }), { target: { value: "60" } });
    expect(chip()).toHaveTextContent("10"); // 60 + 20 against sellable 90

    fireEvent.change(screen.getByRole("spinbutton", { name: "Grade B" }), { target: { value: "30" } });
    expect(chip()).toHaveTextContent("the day adds up");
    // The chip and the button are the same rule (#394) — proven together, since
    // the whole point of sharing lib/grading is that they cannot disagree.
    fireEvent.change(screen.getByLabelText(/Reason/), { target: { value: "recount" } });
    expect(screen.getByRole("button", { name: "Save adjustment" })).toBeEnabled();
  });

  it("reports the overshoot rather than a negative remainder", async () => {
    mockListDailyEntries.mockResolvedValue([SUBMITTED]);
    await openAdjustPanel();

    fireEvent.change(screen.getByRole("spinbutton", { name: "Grade A" }), { target: { value: "95" } });
    // #443 — typing now auto-raises the total to absorb the overshoot (95 +
    // 20 would otherwise reconcile once the total catches up), so pin the
    // total back to its original 100 to force the over state this test
    // asserts on.
    fireEvent.change(screen.getByRole("spinbutton", { name: "Total eggs" }), { target: { value: "100" } });
    // 95 + 20 − 90, as a POSITIVE figure — asserted whole, since a substring
    // match on "25" reads a rendered "-25" as a pass.
    expect(chip()).toHaveTextContent(/^25 over the sellable count$/);
  });

  // A negative sellable makes every derived reading meaningless, so the counts
  // pane says so instead of printing one.
  it("replaces the sellable readout when the losses pass the total", async () => {
    mockListDailyEntries.mockResolvedValue([SUBMITTED]);
    await openAdjustPanel();

    fireEvent.change(screen.getByRole("spinbutton", { name: "Cracked" }), { target: { value: "99" } });

    expect(within(dialog()).getByText(/exceed total eggs/)).toBeInTheDocument();
    expect(within(dialog()).queryByText("Sellable", { exact: false })).not.toBeInTheDocument();
    expect(chip()).toHaveTextContent("Fix the counts first");
    fireEvent.change(screen.getByLabelText(/Reason/), { target: { value: "recount" } });
    expect(screen.getByRole("button", { name: "Save adjustment" })).toBeDisabled();
  });

  // F134's remainder gesture, mirrored here: dragging is unavailable on the
  // phone this is used on and by keyboard, so arming turns each grade row into
  // a plain button. Asserted through the DOM the user gets, not the handler.
  it("hands the whole remainder to one grade line", async () => {
    mockListDailyEntries.mockResolvedValue([SUBMITTED]);
    await openAdjustPanel();

    fireEvent.click(within(dialog()).getByRole("button", { name: /remaining 30/ }));
    fireEvent.click(within(dialog()).getByRole("button", { name: "Put all 30 remaining in Grade B" }));

    // 20 + 30 = 50, and 40 + 50 now equals sellable — so the offer is gone.
    expect(screen.getByRole("spinbutton", { name: "Grade B" })).toHaveValue(50);
    expect(within(dialog()).queryByRole("button", { name: /remaining/ })).not.toBeInTheDocument();
    expect(chip()).toHaveTextContent("the day adds up");
  });

  // The chip's drag payload and the row's drop handler are one contract: the
  // row accepts a drop ONLY for our private type, so a file or a bit of text
  // dragged in from elsewhere can never assign the day.
  it("assigns the remainder on a drop carrying our own payload, and ignores any other", async () => {
    mockListDailyEntries.mockResolvedValue([SUBMITTED]);
    await openAdjustPanel();

    fireEvent.click(within(dialog()).getByRole("button", { name: /remaining 30/ }));
    const gradeBRow = screen.getByRole("spinbutton", { name: "Grade B" }).closest(".entry-row")!;

    // A foreign drag (plain text — what dropping a link or a selection looks
    // like) must leave the line untouched.
    fireEvent.drop(gradeBRow, { dataTransfer: { types: ["text/plain"] } });
    expect(screen.getByRole("spinbutton", { name: "Grade B" })).toHaveValue(20);

    fireEvent.drop(gradeBRow, {
      dataTransfer: { types: ["application/x-cluckwork-remainder", "text/plain"] },
    });
    expect(screen.getByRole("spinbutton", { name: "Grade B" })).toHaveValue(50);
  });

  // Armed and saveable are mutually exclusive — arming needs a remainder, Save
  // needs none — asserted for both routes to zero: the gesture, which disarms
  // itself, and typing, which does not.
  //
  // Scope, stated because it is easy to over-read: this pins the SETTLED state
  // only, and passes whether the render derives `armed` or reads the raw flag
  // (measured). What the derivation buys is the frame before the effect runs,
  // and that needs the raw-dispatch test below; the truth table itself is in
  // lib/grading.test.ts.
  it.each([
    ["the take-remainder control", () =>
      fireEvent.click(within(dialog()).getByRole("button", { name: /Put all 30 remaining in Grade A/ }))],
    ["typing the last grade", () =>
      fireEvent.change(screen.getByRole("spinbutton", { name: "Grade A" }), { target: { value: "70" } })],
  ])("is never armed and saveable at once — reconciled via %s", async (_label, reconcile) => {
    mockListDailyEntries.mockResolvedValue([SUBMITTED]);
    await openAdjustPanel();
    fireEvent.change(screen.getByLabelText(/Reason/), { target: { value: "recount" } });

    // Armed: there is a remainder, so Save is necessarily refused.
    fireEvent.click(within(dialog()).getByRole("button", { name: /remaining 30/ }));
    expect(screen.getByRole("button", { name: "Save adjustment" })).toBeDisabled();

    reconcile();

    expect(screen.getByRole("button", { name: "Save adjustment" })).toBeEnabled();
    expect(within(dialog()).queryByRole("button", { name: /Put all/ })).not.toBeInTheDocument();
    expect(within(dialog()).queryByRole("button", { name: /remaining/ })).not.toBeInTheDocument();
    // The rows stop being drop targets in the same breath, not a frame later.
    expect(dialog().querySelector(".entry-row.taking")).toBeNull();
  });

  // The frame the two tests above CANNOT see, and the one the bug lived in.
  // `fireEvent` wraps every dispatch in act(), which flushes passive effects
  // before the assertions run — so with the disarm effect doing the work, both
  // spellings look identical from a normal test. Dispatching the input event
  // raw skips that flush: React still re-renders the discrete event
  // synchronously, but the effect has not run yet, which is exactly the state a
  // user's next click would land in. Derived `armed` is 0 take-buttons here;
  // reading `assigning` directly renders 2 (measured both ways).
  it("drops the row targets in the same render as the reconciliation, not on the effect", async () => {
    mockListDailyEntries.mockResolvedValue([SUBMITTED]);
    await openAdjustPanel();
    fireEvent.change(screen.getByLabelText(/Reason/), { target: { value: "recount" } });
    fireEvent.click(within(dialog()).getByRole("button", { name: /remaining 30/ }));

    const input = screen.getByRole("spinbutton", { name: "Grade A" }) as HTMLInputElement;
    // React tracks the input's value on the node, so setting `.value` directly
    // is ignored as a no-op change; go through the prototype setter it patched.
    const setValue = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value")!.set!;
    setValue.call(input, "70"); // 70 + 20 === sellable 90
    input.dispatchEvent(new Event("input", { bubbles: true }));

    expect(screen.queryAllByRole("button", { name: /Put all/ })).toHaveLength(0);
    expect(dialog().querySelector(".entry-row.taking")).toBeNull();
    // …and Save is live in that same frame, which is what makes an armed save
    // reachable at all if the two states are allowed to overlap.
    expect(screen.getByRole("button", { name: "Save adjustment" })).toBeEnabled();
  });

  // The derived flag alone would let a typed correction RE-arm rows the user
  // never re-armed: `assigning` stays true in state, so dropping back below
  // sellable would light them up again. The effect is what clears it, and this
  // is the only test that fails if it is deleted.
  it("does not silently re-arm when a correction opens a remainder again", async () => {
    mockListDailyEntries.mockResolvedValue([SUBMITTED]);
    await openAdjustPanel();

    fireEvent.click(within(dialog()).getByRole("button", { name: /remaining 30/ }));
    fireEvent.change(screen.getByRole("spinbutton", { name: "Grade A" }), { target: { value: "70" } });
    // Back under sellable — a remainder exists again, but the gesture was let
    // go, so the rows must stay quiet until it is armed deliberately.
    fireEvent.change(screen.getByRole("spinbutton", { name: "Grade A" }), { target: { value: "50" } });

    expect(within(dialog()).getByRole("button", { name: /remaining 20/ })).toBeInTheDocument();
    expect(within(dialog()).queryByRole("button", { name: /Put all/ })).not.toBeInTheDocument();
  });

  // #443 — the ceiling the steppers used to carry (+ stops at what is
  // unaccounted for) is gone here too: a grade running the total's total
  // out raises the total to match instead of refusing the tap.
  it("no longer stops the + stepper at the unallocated remainder — raises the total to fit instead", async () => {
    mockListDailyEntries.mockResolvedValue([SUBMITTED]);
    await openAdjustPanel();

    // Fixture: sellable 90, graded 40 + 20, so 30 unallocated — the old
    // ceiling on Grade A was 40 + 30 = 70.
    const gradeA = screen.getByRole("spinbutton", { name: "Grade A" });
    fireEvent.change(gradeA, { target: { value: "70" } }); // exactly at the old ceiling
    expect(chip()).toHaveTextContent("the day adds up");

    const plusA = screen.getByRole("button", { name: "Increase grade a" });
    expect(plusA).toBeEnabled();
    fireEvent.pointerDown(plusA);
    fireEvent.pointerUp(plusA);

    expect(screen.getByRole("spinbutton", { name: "Grade A" })).toHaveValue(71);
    // 100 → 101: the total caught up rather than refusing the tap.
    expect(screen.getByRole("spinbutton", { name: "Total eggs" })).toHaveValue(101);
    expect(chip()).toHaveTextContent("the day adds up");
  });

  // Mirrors DailyEntryPage.test.tsx's identical test: a single tap cannot
  // distinguish setLine's gradeQtyRef-based sum from one naively read off
  // the `lineQty` closure, since NumberField's hold-to-repeat binds its
  // WHOLE burst to the one setLine closure captured at press time. Only a
  // genuine multi-tick hold exercises the reason the ref exists (codex
  // review of #449 / adversarial review).
  it("accumulates correctly across a genuine multi-tick hold, not just the press-time snapshot", async () => {
    mockListDailyEntries.mockResolvedValue([SUBMITTED]);
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      await openAdjustPanel();

      const plusA = screen.getByRole("button", { name: "Increase grade a" });
      await act(async () => { fireEvent.pointerDown(plusA); });
      // Same hold length and acceleration curve as NumberField.test.tsx's
      // "accelerates while held" case: press 1 + ticks 1-10 at +1 (10) +
      // ticks 11-16 at +5 (30) = 41 over 1300ms.
      await act(async () => { vi.advanceTimersByTime(1300); });
      await act(async () => { fireEvent.pointerUp(plusA); });

      // Fixture: Grade A starts at 40, Grade B at 20, losses 10.
      expect(screen.getByRole("spinbutton", { name: "Grade A" })).toHaveValue(40 + 41);
      // Every tick in the burst increased the sum, so the total tracked all
      // of them — the read-off-a-stale-closure regression would leave this
      // frozen near the press-time value instead of 40 + 41 + 20 + 10.
      expect(screen.getByRole("spinbutton", { name: "Total eggs" })).toHaveValue(40 + 41 + 20 + 10);
    } finally {
      vi.useRealTimers();
    }
  });

  // codex review of #449: gating only on "still over" (rather than on this
  // EDIT increasing the graded sum) meant correcting an over-graded day by
  // walking a grade back down with − ratcheted the total right back up on
  // every decrement, undoing the admin's own step-1 correction.
  it("does not ratchet the total back up when correcting an over-graded day with −", async () => {
    mockListDailyEntries.mockResolvedValue([SUBMITTED]);
    await openAdjustPanel();

    fireEvent.change(screen.getByRole("spinbutton", { name: "Grade A" }), { target: { value: "70" } }); // 70 + 20 === sellable 90
    expect(chip()).toHaveTextContent("the day adds up");
    fireEvent.change(screen.getByRole("spinbutton", { name: "Total eggs" }), { target: { value: "50" } }); // trimmed directly — now over

    const minusA = screen.getByRole("button", { name: "Decrease grade a" });
    fireEvent.pointerDown(minusA);
    fireEvent.pointerUp(minusA);

    expect(screen.getByRole("spinbutton", { name: "Grade A" })).toHaveValue(69);
    expect(screen.getByRole("spinbutton", { name: "Total eggs" })).toHaveValue(50);
  });

  // A 409 replaces every number in the form with the winner's, because keeping
  // this admin's typed figures could silently clobber a grade line the other
  // one just added. The REASON is the one thing that survives — it is this
  // admin's own justification, and retyping it is pure friction. The behaviour
  // predates this PR; nothing asserted it (pi review of #403).
  it("keeps the typed reason, and only the reason, across a 409 rebind", async () => {
    const WINNER: DailyEntry = {
      ...SUBMITTED, version: 2, totalEggs: 120,
      grades: [{ eggGradeId: "gr1", quantity: 55 }, { eggGradeId: "gr2", quantity: 55 }],
    };
    mockListDailyEntries.mockResolvedValue([SUBMITTED]);
    mockAdjustDailyEntry.mockRejectedValue(new ApiError(409, "Conflict", "conflict"));
    mockGetDailyEntry.mockResolvedValue(WINNER);
    await openAdjustPanel();

    fireEvent.change(screen.getByRole("spinbutton", { name: "Grade A" }), { target: { value: "45" } });
    fireEvent.change(screen.getByRole("spinbutton", { name: "Grade B" }), { target: { value: "45" } });
    fireEvent.change(screen.getByLabelText(/Reason/), { target: { value: "recount after spillage" } });

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Save adjustment" }));
    });

    expect(await screen.findByText(/re-apply your correction/)).toBeInTheDocument();
    // Mine, kept.
    expect(screen.getByLabelText(/Reason/)).toHaveValue("recount after spillage");
    // Theirs, everywhere else — not the 45/45 this admin typed.
    expect(screen.getByRole("spinbutton", { name: "Total eggs" })).toHaveValue(120);
    expect(screen.getByRole("spinbutton", { name: "Grade A" })).toHaveValue(55);
    expect(screen.getByRole("spinbutton", { name: "Grade B" })).toHaveValue(55);
  });

  it("offers nothing to hand out once the day already reconciles", async () => {
    mockListDailyEntries.mockResolvedValue([
      { ...SUBMITTED, grades: [{ eggGradeId: "gr1", quantity: 90 }] },
    ]);
    await openAdjustPanel();

    expect(within(dialog()).queryByRole("button", { name: /remaining/ })).not.toBeInTheDocument();
  });

  // Opening a different entry must not leave the previous one's armed state
  // pointing at rows the user never aimed it at.
  it("disarms the remainder gesture when another entry is opened", async () => {
    mockListDailyEntries.mockResolvedValue([SUBMITTED, LOCKED]);
    renderWithProviders(<HistoryPage />, { token: ADMIN });
    const [firstAdjust, secondAdjust] = await screen.findAllByRole("button", { name: "adjust" });

    fireEvent.click(firstAdjust);
    fireEvent.click(within(dialog()).getByRole("button", { name: /remaining 30/ }));
    expect(within(dialog()).getAllByRole("button", { name: /Put all/ })).toHaveLength(2);

    fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));
    fireEvent.click(secondAdjust);

    expect(within(dialog()).queryByRole("button", { name: /Put all/ })).not.toBeInTheDocument();
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

// F135: voiding used to be a window.prompt whose "reason required" check ran
// only after the popup had closed — a blank answer cost the user everything
// they had typed. It is now a dialog that validates in place.
describe("HistoryPage void — reason dialog", () => {
  const voidDialog = () => screen.getByRole("dialog");

  async function openVoid() {
    mockListDailyEntries.mockResolvedValue([SUBMITTED]);
    renderWithProviders(<HistoryPage />, { token: ADMIN });
    fireEvent.click(await screen.findByRole("button", { name: "void" }));
  }

  it("asks before writing, naming the entry it is about to void", async () => {
    await openVoid();

    expect(voidDialog()).toHaveAccessibleName(
      "Void the 2026-07-19 entry for Hen House 1?");
    expect(vi.mocked(voidDailyEntry)).not.toHaveBeenCalled();
  });

  it("refuses a blank reason inline and keeps the dialog open", async () => {
    await openVoid();

    fireEvent.change(within(voidDialog()).getByLabelText("Reason *"), { target: { value: "   " } });
    await act(async () => {
      fireEvent.click(within(voidDialog()).getByRole("button", { name: "Void entry" }));
    });

    expect(screen.getByText("A reason is required.")).toBeInTheDocument();
    expect(voidDialog()).toBeInTheDocument();
    expect(vi.mocked(voidDailyEntry)).not.toHaveBeenCalled();
  });

  it("sends the trimmed reason with the entry's loaded version", async () => {
    vi.mocked(voidDailyEntry).mockResolvedValue(undefined as never);
    await openVoid();

    fireEvent.change(within(voidDialog()).getByLabelText("Reason *"),
      { target: { value: "  miscounted the trays  " } });
    await act(async () => {
      fireEvent.click(within(voidDialog()).getByRole("button", { name: "Void entry" }));
    });

    expect(vi.mocked(voidDailyEntry)).toHaveBeenCalledWith(
      "de1",
      { version: 1, reason: "miscounted the trays" },
      expect.any(String),
    );
  });

  it("spins the originating row's void button while the void is in flight; siblings disable without spinning (#236)", async () => {
    const SECOND: DailyEntry = { ...SUBMITTED, id: "de9", date: "2026-07-17" };
    mockListDailyEntries.mockResolvedValue([SUBMITTED, SECOND]);
    // Held promise: every assertion here lands INSIDE the pending window.
    let resolveVoid!: () => void;
    vi.mocked(voidDailyEntry).mockReturnValue(new Promise<void>((r) => (resolveVoid = r)) as never);
    renderWithProviders(<HistoryPage />, { token: ADMIN });

    const row1 = await screen.findByRole("row", { name: /2026-07-19/ });
    fireEvent.click(within(row1).getByRole("button", { name: "void" }));
    fireEvent.change(within(voidDialog()).getByLabelText("Reason *"), { target: { value: "dupe" } });
    await act(async () => {
      fireEvent.click(within(voidDialog()).getByRole("button", { name: "Void entry" }));
    });

    // The dialog settled BEFORE the request started (useConfirm contract), so
    // the originating row control is the pending indicator.
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    const voiding = within(row1).getByRole("button", { name: "void" });
    expect(voiding).toBeDisabled();
    expect(voiding).toHaveAttribute("aria-busy", "true");

    // Sibling controls go inert but must NOT spin — exactly one control claims
    // the flight (void:de1), the rest merely disable.
    const row2 = screen.getByRole("row", { name: /2026-07-17/ });
    expect(within(row2).getByRole("button", { name: "void" })).toBeDisabled();
    expect(within(row2).getByRole("button", { name: "void" })).not.toHaveAttribute("aria-busy");
    expect(within(row1).getByRole("button", { name: "adjust" })).toBeDisabled();
    expect(within(row1).getByRole("button", { name: "adjust" })).not.toHaveAttribute("aria-busy");

    await act(async () => resolveVoid());
    // Settled: no pending scope remains, the row control is live again.
    const settled = within(screen.getByRole("row", { name: /2026-07-19/ }))
      .getByRole("button", { name: "void" });
    expect(settled).toBeEnabled();
    expect(settled).not.toHaveAttribute("aria-busy");
  });

  it("writes nothing when the void is dismissed", async () => {
    await openVoid();

    await act(async () => {
      fireEvent.click(within(voidDialog()).getByRole("button", { name: "Cancel" }));
    });

    expect(screen.queryByRole("dialog")).toBeNull();
    expect(vi.mocked(voidDailyEntry)).not.toHaveBeenCalled();
  });
});

// ---------------------------------------------------------------------------
// i18n wiring (#182, Task 27, batch B5)
// ---------------------------------------------------------------------------

// `history` is English-only (not in TRANSLATED_NAMESPACES — see
// translations-status.ts), so under ANY UI language the rendered text falls
// back to this exact English string, same as a still-hardcoded literal would
// render — asserting plain English under default lng:"en" would prove nothing
// (CONTRIBUTING-i18n.md's fallback trap). Swap the catalog value at runtime
// instead, the same i18n.addResource technique the other batches use, so each
// marker only renders if the screen actually reads the catalog rather than a
// literal that happens to still match it.
describe("HistoryPage i18n wiring (#182, Task 27)", () => {
  function withOverride(ns: string, key: string, value: string, run: () => Promise<void> | void) {
    const original = i18n.getResource("en", ns, key) as string;
    i18n.addResource("en", ns, key, value);
    return Promise.resolve(run()).finally(() => {
      i18n.addResource("en", ns, key, original);
    });
  }

  it("reads the heading from the catalog, not a hardcoded literal", async () => {
    await withOverride("history", "title", "TITLE-MARKER", async () => {
      renderWithProviders(<HistoryPage />, { token: ADMIN });
      expect(await screen.findByRole("heading", { name: "TITLE-MARKER" })).toBeInTheDocument();
      expect(screen.queryByRole("heading", { name: "Daily entry history" })).not.toBeInTheDocument();
    });
  });

  it("reads a table column header from the catalog, not a hardcoded literal", async () => {
    await withOverride("history", "dateHeader", "DATE-HEADER-MARKER", async () => {
      mockListDailyEntries.mockResolvedValue([SUBMITTED]);
      renderWithProviders(<HistoryPage />, { token: ADMIN });
      expect(await screen.findByRole("columnheader", { name: "DATE-HEADER-MARKER" })).toBeInTheDocument();
      expect(screen.queryByRole("columnheader", { name: "Date" })).not.toBeInTheDocument();
    });
  });

  it("reads the flock filter label from the catalog, not a hardcoded literal", async () => {
    await withOverride("history", "flockLabel", "FLOCK-LABEL-MARKER", async () => {
      renderWithProviders(<HistoryPage />, { token: ADMIN });
      expect(await screen.findByLabelText("FLOCK-LABEL-MARKER")).toBeInTheDocument();
      expect(screen.queryByLabelText("Flock")).not.toBeInTheDocument();
    });
  });

  // Proves the adjust dialog's title reads the catalog template AND
  // interpolates the entry's free-form date + resolved flock name (DATA) —
  // a hardcoded template literal would never pick up the marker text even
  // though the date/flock would still look right.
  it("interpolates the date and flock into the adjust-dialog title from the catalog", async () => {
    await withOverride("history", "adjustDialogTitleWithEntry", "ADJUST-MARKER {{date}} / {{flock}} END", async () => {
      mockListDailyEntries.mockResolvedValue([SUBMITTED]);
      await openAdjustPanel();
      expect(screen.getByRole("dialog")).toHaveAccessibleName("ADJUST-MARKER 2026-07-19 / Hen House 1 END");
    });
  });

  // Proves the void confirm dialog's title is built with the IMPERATIVE
  // i18n.t() pattern (askReason runs in an event handler, not render) and
  // still reads the catalog template + interpolates date/flock (DATA).
  it("interpolates the date and flock into the void-confirm title from the catalog", async () => {
    await withOverride("history", "voidConfirmTitle", "VOID-MARKER {{date}} / {{flock}} END", async () => {
      mockListDailyEntries.mockResolvedValue([SUBMITTED]);
      renderWithProviders(<HistoryPage />, { token: ADMIN });
      fireEvent.click(await screen.findByRole("button", { name: "void" }));
      expect(screen.getByRole("dialog")).toHaveAccessibleName("VOID-MARKER 2026-07-19 / Hen House 1 END");
    });
  });

  // Proves the Voided pill's bespoke <span> reads the catalog, not the
  // hardcoded literal the pre-sweep component had — voidReason stays raw DATA
  // on the `title` attribute either way.
  it("reads the Voided status pill from the catalog, not a hardcoded literal", async () => {
    await withOverride("history", "statusVoided", "VOIDED-BADGE-MARKER", async () => {
      mockListDailyEntries.mockResolvedValue([VOIDED]);
      renderWithProviders(<HistoryPage />, { token: ADMIN });
      const row = await screen.findByRole("row", { name: /Hen House 1/ });
      expect(within(row).getByText("VOIDED-BADGE-MARKER")).toBeInTheDocument();
      expect(within(row).queryByText("Voided")).not.toBeInTheDocument();
      expect(within(row).getByTitle("spoiled")).toBeInTheDocument(); // voidReason: raw DATA, untouched
    });
  });

  // Proves the default (Submitted/Draft) branch passes a translated `label`
  // into StatusBadge rather than relying on the raw `status` prop — StatusBadge
  // renders `label ?? status`, so a hardcoded/omitted label would still show
  // "Submitted" today (identity text) but would NOT pick up this override.
  it("reads the Submitted status label from the catalog via StatusBadge's label prop", async () => {
    await withOverride("history", "statusSubmitted", "SUBMITTED-MARKER", async () => {
      mockListDailyEntries.mockResolvedValue([SUBMITTED]);
      renderWithProviders(<HistoryPage />, { token: ADMIN });
      const row = await screen.findByRole("row", { name: /Hen House 1/ });
      expect(within(row).getByText("SUBMITTED-MARKER")).toBeInTheDocument();
      expect(within(row).queryByText("Submitted")).not.toBeInTheDocument();
    });
  });

  // Explicit requirement: the ManagerAdjusted pill is an intentional
  // harmonization, not a text-preserving retrofit — the raw wire status stays
  // "ManagerAdjusted", but the pill has always read "Adjusted" (matching the
  // shared enums:status.ManagerAdjusted label Dashboard's retrofit adopted;
  // see the `enums` namespace header comment in en.ts). This is a correctness
  // assertion under the real (default) catalog; the paired override test
  // below proves the text is catalog-sourced rather than still hardcoded.
  it("renders the ManagerAdjusted entry's status pill as 'Adjusted', not the raw status", async () => {
    mockListDailyEntries.mockResolvedValue([MANAGER_ADJUSTED]);
    renderWithProviders(<HistoryPage />, { token: ADMIN });
    const row = await screen.findByRole("row", { name: /Hen House 1/ });
    expect(within(row).getByText("Adjusted")).toBeInTheDocument();
    expect(within(row).queryByText("ManagerAdjusted")).not.toBeInTheDocument();
  });

  it("reads the ManagerAdjusted ('Adjusted') status pill from the catalog, not a hardcoded literal", async () => {
    await withOverride("history", "statusAdjusted", "ADJUSTED-BADGE-MARKER", async () => {
      mockListDailyEntries.mockResolvedValue([MANAGER_ADJUSTED]);
      renderWithProviders(<HistoryPage />, { token: ADMIN });
      const row = await screen.findByRole("row", { name: /Hen House 1/ });
      expect(within(row).getByText("ADJUSTED-BADGE-MARKER")).toBeInTheDocument();
      expect(within(row).queryByText("Adjusted")).not.toBeInTheDocument();
      expect(within(row).getByTitle("recount")).toBeInTheDocument(); // adjustReason: raw DATA, untouched
    });
  });

  // Explicit requirement: the Locked pill's tooltip interpolates the raw
  // lockedAtUtc timestamp (DATA) into the catalog's "Locked {{time}}"
  // template (COPY) — a correctness assertion under the real (default)
  // catalog that the substitution actually happens, not just that the
  // template string exists.
  it("interpolates the locked timestamp into the Locked pill's tooltip", async () => {
    mockListDailyEntries.mockResolvedValue([LOCKED]);
    renderWithProviders(<HistoryPage />, { token: ADMIN });
    await screen.findByRole("row", { name: /Hen House 1/ });
    expect(screen.getByTitle("Locked 2026-07-19T08:00:00Z")).toBeInTheDocument();
  });

  // Paired wiring proof: overriding the TEMPLATE (not just the value the
  // {{time}} var carries) shows up verbatim around the interpolated
  // timestamp — a hardcoded `Locked ${e.lockedAtUtc}` template literal could
  // never pick up MARKER text on either side of the timestamp.
  it("reads the lockedAt tooltip template from the catalog, not a hardcoded literal", async () => {
    await withOverride("history", "lockedAt", "TIME-MARKER {{time}} MARKER-END", async () => {
      mockListDailyEntries.mockResolvedValue([LOCKED]);
      renderWithProviders(<HistoryPage />, { token: ADMIN });
      await screen.findByRole("row", { name: /Hen House 1/ });
      expect(screen.getByTitle("TIME-MARKER 2026-07-19T08:00:00Z MARKER-END")).toBeInTheDocument();
      expect(screen.queryByTitle(/^Locked /)).not.toBeInTheDocument();
    });
  });

  // Imperative i18n.t() — the mount-effect .catch for the entries list (the
  // load() useCallback runs as a Promise callback, not render).
  it("reads the load-entries error from the catalog, not a hardcoded literal", async () => {
    mockListDailyEntries.mockRejectedValue(new Error("boom"));
    await withOverride("history", "loadEntriesFailed", "LOAD-ENTRIES-ERROR-MARKER", async () => {
      renderWithProviders(<HistoryPage />, { token: ADMIN });
      expect(await screen.findByText("LOAD-ENTRIES-ERROR-MARKER")).toBeInTheDocument();
      expect(screen.queryByText(/Could not load entries/)).not.toBeInTheDocument();
    });
  });

  // Imperative i18n.t() with interpolation — the "nothing left to adjust"
  // 409-rebind message, which interpolates the fresh entry's lowercased raw
  // status (see the locale-fragile note in en.ts and at the call site).
  it("interpolates the lowercased status into the nothing-to-adjust message from the catalog", async () => {
    mockListDailyEntries.mockResolvedValue([SUBMITTED]);
    mockAdjustDailyEntry.mockRejectedValue(new ApiError(409, "Conflict", "conflict"));
    mockGetDailyEntry.mockResolvedValue({ ...VOIDED, status: "Voided" });
    await withOverride("history", "nothingToAdjustMessage", "NOW-{{status}}-MARKER", async () => {
      await openAdjustPanel();
      // #394: Save stays disabled until grading reconciles — bring the
      // stored 40 + 20 up to the 90 sellable so the click actually reaches
      // the (mocked, 409-rejecting) API call this test is about.
      fireEvent.change(screen.getByRole("spinbutton", { name: "Grade A" }), { target: { value: "45" } });
      fireEvent.change(screen.getByRole("spinbutton", { name: "Grade B" }), { target: { value: "45" } });
      fireEvent.change(screen.getByLabelText(/Reason/), { target: { value: "recount" } });
      await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: "Save adjustment" }));
      });
      expect(await screen.findByText("NOW-voided-MARKER")).toBeInTheDocument();
    });
  });
});
