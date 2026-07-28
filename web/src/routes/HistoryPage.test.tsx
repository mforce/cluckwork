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
    fireEvent.change(screen.getByRole("spinbutton", { name: "Grade A" }), { target: { value: "46" } });
    fireEvent.change(screen.getByRole("spinbutton", { name: "Grade B" }), { target: { value: "45" } });
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

    fireEvent.change(screen.getByRole("spinbutton", { name: "Grade A" }), { target: { value: "45" } });
    fireEvent.change(screen.getByRole("spinbutton", { name: "Grade B" }), { target: { value: "45" } }); // 90 === sellable
    fireEvent.change(screen.getByLabelText(/Reason/), { target: { value: "recount" } });
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
      fireEvent.change(screen.getByLabelText(/Reason/), { target: { value: "recount" } });
      await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: "Save adjustment" }));
      });
      expect(await screen.findByText("NOW-voided-MARKER")).toBeInTheDocument();
    });
  });
});
