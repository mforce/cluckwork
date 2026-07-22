import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, within, fireEvent, waitFor, act } from "@testing-library/react";
import { DailyEntryPage } from "./DailyEntryPage";
import {
  listFlocks, listEggGrades, listDailyEntries, createFlock, recordDailyEntry, submitDailyEntry,
} from "../api/cluckwork";
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
    fireEvent.change(within(dialog()).getByLabelText("Birds"), { target: { value: "250" } });
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

    // The numeral is aria-hidden, so the accessible name is the words alone.
    expect(screen.getByRole("heading", { name: "Flock & date" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Egg counts" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Sellable production by grade" }))
      .toBeInTheDocument();
  });

  it("keeps the math line and both saves together in the footer", async () => {
    await renderReady();
    const foot = document.querySelector(".entry-foot")!;

    expect(within(foot as HTMLElement).getByText(/Graded \d+ of \d+ sellable/)).toBeInTheDocument();
    expect(within(foot as HTMLElement).getByRole("button", { name: /Save draft/ })).toBeInTheDocument();
    expect(within(foot as HTMLElement).getByRole("button", { name: /Save & submit/ })).toBeInTheDocument();
  });

  it("shows the losses error in the footer in place of the math line", async () => {
    await renderReady();
    setNum("Total eggs", 10);
    setNum("Cracked", 20);

    const foot = document.querySelector(".entry-foot") as HTMLElement;
    expect(within(foot).getByText(/exceed total eggs/)).toBeInTheDocument();
    expect(within(foot).queryByText(/Graded \d+ of \d+ sellable/)).toBeNull();
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

  it("stays absent on a locked day, which has its own banner", async () => {
    mockListDailyEntries.mockResolvedValue([
      { ...draftFor(todayIso()), status: "Submitted" },
    ]);
    render(<DailyEntryPage />);

    expect(await screen.findByText(/already submitted/)).toBeInTheDocument();
    expect(screen.queryByText("Editing draft")).toBeNull();
    expect(screen.getByLabelText("Total eggs")).toBeDisabled();
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
    expect(within(screen.getByRole("dialog")).getByText("Flock name already used."))
      .toBeInTheDocument();
    expect(screen.getByRole("dialog")).toBeInTheDocument(); // stays open to retry
  });
});
