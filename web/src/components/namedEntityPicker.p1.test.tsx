// #512 P1 remediation — causal regression tests for the four Opus-review
// findings in the shared picker engine. One test per finding, each driving a
// deferred promise at the exact race point:
//
//   1. The single generation token conflates discovery and selection
//      transitions: a selection transition (commit) that lands while a
//      discovery is in flight makes that still-newest discovery's LATE
//      failure drop instead of surfacing. Discovery and selection-transition
//      generations must be independent.
//   2. Committing an option while a Load-more extension is in flight must not
//      cancel or wedge the extension — the extension settles and the live
//      region / Load more button recover.
//   3. An outside mousedown restores the committed/blank text and invokes
//      onOutsideClick, but MUST NOT clear discovery items/cursor/phase or bump
//      the discovery generation — an always-open picker must retain its
//      options and remain usable.
//   4. The results announcement must be the i18next `{{count}}` interpolation
//      (exact "3 results", not a template leak or regex match).
//
// Real timers throughout (the 250 ms debounce crosses a 300 ms real wait) so
// the US1 fake-timer suite in NamedEntityPicker.test.tsx stays independent.
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, fireEvent, waitFor, act } from "@testing-library/react";

import { FlockPicker } from "./FlockPicker";
import { listFlocks } from "../api/cluckwork";
import type { Flock } from "../api/cluckwork";
import i18n from "../i18n";

vi.mock("../api/cluckwork", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api/cluckwork")>();
  return { ...actual, listFlocks: vi.fn() };
});
const mockListFlocks = vi.mocked(listFlocks);

const NO_HISTORY = { createdByEmail: null, createdAtUtc: null, lastChangedByEmail: null, lastChangedAtUtc: null };
const F = (id: string, name: string): Flock => ({
  ...NO_HISTORY, id, name, farmId: "farm", houseId: "h", breed: "B",
  placementDate: "2026-01-01", initialCount: 10, currentBirds: 9, status: "Active",
});
const FLOCKS: Flock[] = Array.from({ length: 122 }, (_, i) => F(`f${i}`, `Flock ${String(i + 1).padStart(2, "0")}`));
const serveFlocks = async (p: { search?: string | null; limit?: number; offset?: number } | undefined): Promise<Flock[]> => {
  const params = p ?? {};
  const q = params.search?.trim() ?? "";
  const filtered = q ? FLOCKS.filter((f) => f.name.toLowerCase().includes(q.toLowerCase())) : FLOCKS;
  return filtered.slice(params.offset ?? 0, (params.offset ?? 0) + (params.limit ?? 50));
};

beforeEach(() => {
  vi.useRealTimers();
  mockListFlocks.mockReset();
  mockListFlocks.mockImplementation(async (p) => serveFlocks(p));
});
afterEach(() => { vi.useRealTimers(); });

describe("#512 P1-1: independent discovery / selection-transition generations", () => {
  it("a controlled transition while a replacement is in flight must not drop that newest discovery's late success — the rows settle and no Loading latch", async () => {
    // Deferred promise: the searched replacement never resolves on its own.
    // Sequence: initial discovery settles → the page drives a controlled sync
    // (controlledCommitted + controlledGeneration — a SELECTION transition)
    // → user types "Flock 5" (a NEWER discovery) → its replacement is in
    // flight → it resolves LATE. Under the single shared counter the
    // controlled sync already bumped it, so the still-newest discovery's
    // response is treated as stale and dropped — the list stays empty and
    // the phase latches "replacing" (Loading forever). With independent
    // tokens the completion belongs to the newest discovery: the rows settle
    // and the loading status clears.
    let releaseSearch!: (rows: Flock[]) => void;
    mockListFlocks.mockImplementation((p) => {
      const params = p ?? {};
      if (params.search) {
        return new Promise<Flock[]>((r) => { releaseSearch = r; });
      }
      return serveFlocks(p);
    });
    const onSnapshot = vi.fn();
    // The page drives a controlled sync (controlledCommitted +
    // controlledGeneration) — a SELECTION transition that preserves the
    // discovery window (same entity already in the list).
    const view = ({ gen, entity }: { gen: number; entity: Flock }) => (
      <FlockPicker label="Pick" eligibility="active" required open
        controlledCommitted={entity} controlledGeneration={gen} onSnapshot={onSnapshot} />
    );
    const r = render(view({ gen: 1, entity: FLOCKS[0] }));
    const input = screen.getByRole("combobox");
    // Initial discovery settles; the controlled sync admits Flock 01.
    await waitFor(() => { expect(onSnapshot.mock.calls.at(-1)?.[0]?.committed?.id).toBe("f0"); });
    // Newer discovery: type a query; its replacement is gated.
    fireEvent.change(input, { target: { value: "Flock 5" } });
    await act(async () => { await new Promise((r) => setTimeout(r, 300)); });
    expect(mockListFlocks).toHaveBeenCalledTimes(2);
    expect(mockListFlocks).toHaveBeenLastCalledWith({ search: "Flock 5", eligibility: "active", limit: 50, offset: 0 });
    // A SELECTION transition (controlled sync to another entity) lands while
    // that discovery is in flight. Under the single shared counter this
    // bumps the counter and the gated replacement's completion is then
    // treated as stale; with independent tokens it still settles.
    r.rerender(view({ gen: 2, entity: FLOCKS[5] }));
    await act(async () => {});
    // The newest discovery resolves LATE: its rows must still settle.
    await act(async () => { releaseSearch(FLOCKS.filter((f) => f.name.includes("Flock 5"))); });
    await waitFor(() => { expect(screen.getByRole("option", { name: "Flock 51" })).toBeInTheDocument(); });
    // No Loading latch: the loading status is gone.
    await waitFor(() => { expect(screen.queryByRole("status")).toBeNull(); });
  });

  it("a selection transition while a replacement is in flight must not drop that newest discovery's late failure — the error surfaces and Retry recovers", async () => {
    // Deferred promise: the searched replacement never resolves on its own.
    // Sequence: initial discovery settles → user commits an option (a
    // SELECTION transition) → user types "Flock 99" (a NEWER discovery) →
    // its replacement is in flight → it fails LATE. Under the single shared
    // counter the commit already bumped it, so the still-newest discovery's
    // rejection is treated as stale and silently dropped (no error, no Retry,
    // empty list, phase wedged). With independent tokens the failure belongs
    // to the newest discovery: it is surfaced and its Retry re-runs the
    // replacement.
    let rejectSearch!: (err: Error) => void;
    mockListFlocks.mockImplementation((p) => {
      const params = p ?? {};
      if (params.search) {
        return new Promise<Flock[]>((_r, rej) => { rejectSearch = rej; });
      }
      return serveFlocks(p);
    });
    render(<FlockPicker label="Pick" eligibility="active" required open />);
    const input = screen.getByRole("combobox");
    await waitFor(() => { expect(screen.getByRole("option", { name: "Flock 01" })).toBeInTheDocument(); });
    // 2 calls so far: the initial unfiltered discovery.
    expect(mockListFlocks).toHaveBeenCalledTimes(1);
    // Selection transition: commit an option.
    fireEvent.click(screen.getByRole("option", { name: "Flock 01" }));
    await act(async () => {});
    // Newer discovery: type a query.
    fireEvent.change(input, { target: { value: "Flock 99" } });
    await act(async () => { await new Promise((r) => setTimeout(r, 300)); });
    expect(mockListFlocks).toHaveBeenCalledTimes(2);
    expect(mockListFlocks).toHaveBeenLastCalledWith({ search: "Flock 99", eligibility: "active", limit: 50, offset: 0 });
    // The newest discovery fails LATE.
    await act(async () => { rejectSearch(new Error("net down")); });
    await act(async () => {});
    // The failure surfaces — it is the newest discovery's own failure.
    const alert = document.querySelector<HTMLElement>("[role='alert']");
    expect(alert).not.toBeNull();
    expect(alert).toHaveTextContent(i18n.t("namedEntityPicker:searchFailed"));
    // Retry re-runs the same replacement and recovers; focus returns to the input.
    mockListFlocks.mockImplementation(async (p) => serveFlocks(p));
    input.focus();
    fireEvent.click(screen.getByRole("button", { name: i18n.t("namedEntityPicker:retry") }));
    await waitFor(() => { expect(screen.getByRole("option", { name: "Flock 99" })).toBeInTheDocument(); });
    expect(mockListFlocks).toHaveBeenCalledTimes(3);
    expect(document.activeElement).toBe(input);
  });
});

describe("#512 P1-2: commit does not cancel or wedge an in-flight extension", () => {
  it("committing an option while Load more is in flight lets the extension settle; live state and Load more recover", async () => {
    // Deferred promise: page one resolves, the extension (page two) is gated.
    // Committing an option from page one while the extension is pending must
    // not drop the extension's response — the rows settle, the loading
    // status clears, and Load more re-enables (full 50-row page).
    let releasePageTwo!: (rows: Flock[]) => void;
    mockListFlocks.mockImplementation((p) => {
      const params = p ?? {};
      if ((params.offset ?? 0) === 0) return serveFlocks(p);
      return new Promise<Flock[]>((r) => { releasePageTwo = r; });
    });
    render(<FlockPicker label="Pick" eligibility="active" required open />);
    await waitFor(() => { expect(screen.getByRole("option", { name: "Flock 01" })).toBeInTheDocument(); });
    fireEvent.click(screen.getByRole("button", { name: i18n.t("namedEntityPicker:loadMore") }));
    await waitFor(() => { expect(mockListFlocks).toHaveBeenCalledTimes(2); });
    // The extension is in flight (loading status visible).
    expect(screen.getByRole("status")).toHaveTextContent(i18n.t("namedEntityPicker:loading"));
    // Selection transition: commit a page-one option NOW.
    fireEvent.click(screen.getByRole("option", { name: "Flock 01" }));
    await act(async () => {});
    expect(screen.getByRole("combobox")).toHaveValue("Flock 01");
    // Resolve the extension: its 50 rows must settle.
    await act(async () => { releasePageTwo(FLOCKS.slice(50, 100)); });
    await waitFor(() => { expect(screen.getByRole("option", { name: "Flock 99" })).toBeInTheDocument(); });
    // Live state recovered: no Loading latch, Load more re-enabled (full page).
    await waitFor(() => { expect(screen.queryByRole("status")).toBeNull(); });
    const loadMore = screen.getByRole("button", { name: i18n.t("namedEntityPicker:loadMore") });
    expect(loadMore).not.toBeDisabled();
  });
});

describe("#512 P1-3: outside mousedown keeps discovery alive", () => {
  it("restores committed text and fires onOutsideClick, but does NOT clear the discovery window — options stay visible with no further typing", async () => {
    // Causal guard: rows are VISIBLY PRESENT (the unfiltered initial page,
    // 50 options) at the moment of the outside mousedown, and nothing is
    // typed afterward. A wiping handler (items: [], hasMore: false,
    // cursor: 0, phase: "closed") makes the options vanish from THIS
    // assertion directly — no re-typing needed to notice it, which is what
    // let the previous version of this test pass even when the wipe
    // regression was present (the re-typed query re-triggered discovery
    // regardless of whether the prior window had been wiped).
    const onOutsideClick = vi.fn();
    render(<FlockPicker label="Pick" eligibility="active" required open onOutsideClick={onOutsideClick} />);
    await waitFor(() => { expect(screen.getAllByRole("option")).toHaveLength(50); });
    // Commit Flock 01 so the restore target is a name, not blank — commit
    // retains the discovery window (US1 contract), so this must not itself
    // change the row count.
    fireEvent.click(screen.getByRole("option", { name: "Flock 01" }));
    await act(async () => {});
    const input = screen.getByRole("combobox");
    expect(input).toHaveValue("Flock 01");
    expect(screen.getAllByRole("option")).toHaveLength(50);
    const loadMoreBefore = screen.getByRole("button", { name: i18n.t("namedEntityPicker:loadMore") });
    expect(loadMoreBefore).not.toBeDisabled(); // hasMore still true (122 rows, page 50)

    // Outside mousedown while the picker STAYS OPEN (this fixture's `open`
    // is a static true — the page never closes it on this callback).
    const outside = document.createElement("div");
    document.body.appendChild(outside);
    fireEvent.mouseDown(outside);
    await act(async () => {});

    expect(onOutsideClick).toHaveBeenCalledTimes(1);
    // The committed name is restored (unchanged here — already committed).
    expect(input).toHaveValue("Flock 01");
    // No typing since the mousedown: the options remaining is proof the
    // handler did not clear items/cursor/hasMore/phase.
    expect(screen.getAllByRole("option")).toHaveLength(50);
    expect(screen.getByRole("option", { name: "Flock 01" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: i18n.t("namedEntityPicker:loadMore") })).not.toBeDisabled();
    outside.remove();
  });
});

describe("#512 P1-4: results announcement uses {{count}} interpolation", () => {
  it("announces the EXACT interpolated string \"3 results\" — no template leak, no regex", async () => {
    mockListFlocks.mockReset();
    mockListFlocks.mockResolvedValue([F("g0", "G0"), F("g1", "G1"), F("g2", "G2")]);
    render(<FlockPicker label="Pick" eligibility="active" required open />);
    const region = document.querySelector<HTMLElement>(".named-picker-live");
    expect(region).not.toBeNull();
    await waitFor(() => {
      expect(region!.textContent).toBe(i18n.t("namedEntityPicker:results", { count: 3 }));
    });
    expect(region!.textContent).not.toMatch(/\{|\}/);
  });
});
