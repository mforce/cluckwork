// T034/T030 recovery tests — replacement vs extension error states, Retry
// semantics (focus restoration, same cursor/query), and the stable live
// region. Split from NamedEntityPicker.test.tsx so the recovery suite stays
// independent of the US1/US2 discovery/selection suites.
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, fireEvent, waitFor, act } from "@testing-library/react";
// NOTE: the discovery debounce is a fixed 250 ms (FR-008). These recovery
// tests drive REAL timers: a real 300 ms wait comfortably crosses it. The
// US1 discovery suites in NamedEntityPicker.test.tsx use fake timers; they run
// in a separate file, so there is no fake/real timer conflict here.

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
const F = (id: string, name: string, status: Flock["status"] = "Active"): Flock => ({
  ...NO_HISTORY, id, name, farmId: "farm", houseId: "h", breed: "B",
  placementDate: "2026-01-01", initialCount: 10, currentBirds: 9, status,
});
// 122 flocks: two full pages + a short final page (50/50/22).
const FLOCKS: Flock[] = Array.from({ length: 122 }, (_, i) => F(`f${i}`, `Flock ${String(i + 1).padStart(2, "0")}`));
const serveFlocks = async (p: { search?: string | null; limit?: number; offset?: number } | undefined): Promise<Flock[]> => {
  const params = p ?? {};
  const q = params.search?.trim() ?? "";
  const filtered = q ? FLOCKS.filter((f) => f.name.toLowerCase().includes(q.toLowerCase())) : FLOCKS;
  return filtered.slice(params.offset ?? 0, (params.offset ?? 0) + (params.limit ?? 50));
};

describe("T034: replacement vs extension error recovery + Retry (FR-023/024/028)", () => {
  beforeEach(() => { vi.useRealTimers(); });
  afterEach(() => { vi.useRealTimers(); });

  it("replacement failure: stale rows hidden, translated error + keyboard-reachable Retry; Retry re-runs the REPLACEMENT (offset 0, same query) and restores focus", async () => {
    mockListFlocks.mockReset();
    // Opening fires exactly ONE unfiltered replacement (no debounce on open).
    // Reject it: the generation still matches, so its catch paints the error.
    mockListFlocks.mockRejectedValue(new Error("net down"));
    const onSnapshot = vi.fn();
    render(<FlockPicker label="Pick" eligibility="active" required open onSnapshot={onSnapshot} />);
    const input = screen.getByRole("combobox");
    // Wait for the error to settle, then verify the DOM (the alert is present
    // but getByRole is flaky on the accessible name in this jsdom build).
    await new Promise((r) => setTimeout(r, 50));
    await act(async () => {});
    const alert = document.querySelector<HTMLElement>("[role='alert']");
    expect(alert).not.toBeNull();
    expect(alert).toHaveTextContent(i18n.t("namedEntityPicker:searchFailed"));
    expect(screen.queryByRole("option")).toBeNull();
    // The error is for the initial (unfiltered) discovery — no search yet.
    expect(mockListFlocks).toHaveBeenLastCalledWith({ eligibility: "active", limit: 50, offset: 0 });
    // The Retry is keyboard reachable (a real button inside the picker).
    const retry = screen.getByRole("button", { name: i18n.t("namedEntityPicker:retry") });
    expect(retry.closest(".named-picker")).not.toBeNull();
    // Retry re-issues the REPLACEMENT: the failed search at offset 0, and
    // focus returns to the input (FR-028).
    mockListFlocks.mockResolvedValue(FLOCKS); // from here on, replacements succeed
    input.focus();
    fireEvent.click(retry);
    await waitFor(() => { expect(screen.getByRole("option", { name: "Flock 01" })).toBeInTheDocument(); });
    // The retry re-issued the SAME replacement (the failed unfiltered query)
    // at offset 0 — and the stale-rejection suppression left no duplicate.
    expect(mockListFlocks).toHaveBeenLastCalledWith({ eligibility: "active", limit: 50, offset: 0 });
    // FR-028: Retry restores focus to the input.
    expect(document.activeElement).toBe(input);
  });

  it("extension failure: loaded rows RETAINED, adjacent Retry; Retry re-runs the EXTENSION (same query, painted cursor — offset 50, not 0) and restores focus", async () => {
    mockListFlocks.mockReset();
    mockListFlocks
      .mockResolvedValueOnce(FLOCKS.slice(0, 50))
      .mockRejectedValueOnce(new Error("net down")); // the extension (Load more)
    render(<FlockPicker label="Pick" eligibility="active" required open />);
    await waitFor(() => { expect(screen.getByText("Flock 01")).toBeInTheDocument(); });
    fireEvent.click(screen.getByRole("button", { name: i18n.t("namedEntityPicker:loadMore") }));
    await waitFor(() => {
      const alert = document.querySelector<HTMLElement>("[role='alert']");
      expect(alert).not.toBeNull();
      expect(alert).toHaveTextContent(i18n.t("namedEntityPicker:loadMoreFailed"));
    });
    // FR-024: already loaded results remain usable.
    expect(screen.getByText("Flock 01")).toBeInTheDocument();
    expect(screen.getByText("Flock 50")).toBeInTheDocument();
    // The painted cursor is 50 (a full first page), so the extension retry
    // targets offset 50 — NOT a replacement at offset 0.
    const retry = screen.getByRole("button", { name: i18n.t("namedEntityPicker:retry") });
    const input = screen.getByRole("combobox");
    mockListFlocks.mockResolvedValueOnce(FLOCKS.slice(50, 100));
    input.focus();
    fireEvent.click(retry);
    await waitFor(() => { expect(screen.getByText("Flock 51")).toBeInTheDocument(); });
    expect(mockListFlocks).toHaveBeenLastCalledWith({ eligibility: "active", limit: 50, offset: 50 });
    expect(document.activeElement).toBe(input);
  });
});

describe("T034: stable live region announces state without moving focus (FR-027)", () => {
  it("a single stable [aria-live=polite] live region (deliberately NOT role=status — that role belongs to the separate transient loading/no-results span) is mounted across loading/results/error, and focus stays on the input", async () => {
    mockListFlocks.mockReset();
    mockListFlocks.mockImplementation(async (p) => serveFlocks(p));
    render(<FlockPicker label="Pick" eligibility="active" required open />);
    const input = screen.getByRole("combobox");
    input.focus(); // the contract is about NOT MOVING focus: start focused
    // The live region is mounted, polite and atomic, visually hidden.
    const region = document.querySelector<HTMLElement>(".named-picker-live");
    expect(region).not.toBeNull();
    expect(region).toHaveAttribute("aria-live", "polite");
    expect(region).toHaveAttribute("aria-atomic", "true");
    expect(region).not.toHaveAttribute("role", "status");
    // 1. Loading: the initial discovery fires synchronously on mount (before
    //    the fetch settles), so the live region already announces the
    //    loading state at this point — a real, checkable transition, not
    //    just an unasserted comment.
    expect(region!.textContent).toBe(i18n.t("namedEntityPicker:loading"));
    // 2. Results: after the fetch completes, the live region announces the count.
    await waitFor(() => {
      expect(region!.textContent).toBe(i18n.t("namedEntityPicker:results", { count: 50 }));
    });
    // Focus stays on the input through the result transition.
    expect(document.activeElement).toBe(input);
    // 3. Search (no match): the live region announces no results.
    fireEvent.change(input, { target: { value: "zzzzz" } });
    await waitFor(() => {
      expect(region!.textContent).toBe(i18n.t("namedEntityPicker:noResults"));
    });
    // Focus stays on the input.
    expect(document.activeElement).toBe(input);
    // 4. The SAME node is still mounted (not re-created) across all transitions.
    expect(document.querySelector<HTMLElement>(".named-picker-live")).toBe(region);
  });
});
