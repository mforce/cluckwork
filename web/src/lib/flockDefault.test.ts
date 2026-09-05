import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { rememberFlockId, resolveDefaultFlock } from "./flockDefault";
import { bindAccount, clearBoundAccount } from "../auth/tokenStore";
import { getFlock, listFlocks } from "../api/cluckwork";
import type { Flock } from "../api/cluckwork";
import { NO_RECORD_HISTORY } from "../test/fixtures";

vi.mock("../api/cluckwork", () => ({
  getFlock: vi.fn(),
  listFlocks: vi.fn(),
}));

const mockGetFlock = vi.mocked(getFlock);
const mockListFlocks = vi.mocked(listFlocks);

const flock = (id: string, name: string, status: string): Flock => ({
  ...NO_RECORD_HISTORY,
  id, farmId: "farm1", houseId: "h1", name, breed: "ISA",
  placementDate: "2026-01-01", initialCount: 100, currentBirds: 98, status,
});

const ACTIVE = flock("fl1", "Coop A", "Active");
const ACTIVE_2 = flock("fl2", "Coop B", "Active");
const DEPLETED = flock("fl9", "Old Coop", "Depleted");
const ARCHIVED = flock("fl8", "Gone Coop", "Archived");

// #646 — the flock a capture screen opens on. The bug was sourcing: all three
// screens scanned a page of 100 name-ordered flocks they held for other
// reasons, so on a bigger farm the default was "whatever sorts early".
beforeEach(() => {
  clearBoundAccount();
  localStorage.clear();
  vi.clearAllMocks();
  bindAccount("11111111-1111-1111-1111-111111111111");
  mockListFlocks.mockResolvedValue([]);
});

afterEach(() => {
  clearBoundAccount();
  vi.restoreAllMocks();
});

describe("resolveDefaultFlock (#646)", () => {
  it("prefers the flock last recorded against, when it is on the page", async () => {
    rememberFlockId("fl2");

    expect(await resolveDefaultFlock([ACTIVE, ACTIVE_2])).toBe(ACTIVE_2);
    // No round trip: the answer was already in hand.
    expect(mockGetFlock).not.toHaveBeenCalled();
    expect(mockListFlocks).not.toHaveBeenCalled();
  });

  // The case that motivated the issue: the remembered flock is NOT on the
  // capped page, and the old `f.some(...)` gate dropped it silently.
  it("resolves a remembered flock that is off the page by an exact GET", async () => {
    rememberFlockId("fl-far");
    const offPage = flock("fl-far", "Zulu Coop", "Active");
    mockGetFlock.mockResolvedValue(offPage);

    expect(await resolveDefaultFlock([ACTIVE])).toBe(offPage);
    expect(mockGetFlock).toHaveBeenCalledWith("fl-far");
  });

  it("ignores a remembered flock that has since been archived", async () => {
    rememberFlockId(ARCHIVED.id);
    mockGetFlock.mockResolvedValue(ARCHIVED);

    expect(await resolveDefaultFlock([ACTIVE])).toBe(ACTIVE);
  });

  it("ignores a remembered flock that no longer resolves at all", async () => {
    rememberFlockId("deleted");
    mockGetFlock.mockRejectedValue(new Error("404"));

    expect(await resolveDefaultFlock([ACTIVE])).toBe(ACTIVE);
  });

  it("falls back to the first ACTIVE flock on the page, never a depleted one", async () => {
    expect(await resolveDefaultFlock([DEPLETED, ACTIVE])).toBe(ACTIVE);
    expect(mockListFlocks).not.toHaveBeenCalled();
  });

  // The #646 defect itself: a page whose 100 name-ordered rows contain no
  // active flock. Scanning it yielded nothing (or a depleted stand-in) even
  // when the farm had active flocks further down the alphabet.
  it("asks the server for the first active flock when the page holds none", async () => {
    const offPage = flock("fl-z", "Zulu Coop", "Active");
    mockListFlocks.mockResolvedValueOnce([offPage]);

    expect(await resolveDefaultFlock([ARCHIVED])).toBe(offPage);
    // limit: 1 — one row IS "the first active flock" under the server's name
    // ordering, with no page to fall off the end of.
    expect(mockListFlocks).toHaveBeenCalledWith({ eligibility: "active", limit: 1 });
  });

  it("falls back to a depleted flock only once no active one exists anywhere", async () => {
    mockListFlocks
      .mockResolvedValueOnce([])                 // no active flock at all
      .mockResolvedValueOnce([DEPLETED]);        // …but a depleted one exists

    expect(await resolveDefaultFlock([])).toBe(DEPLETED);
    expect(mockListFlocks).toHaveBeenNthCalledWith(2, { eligibility: "active-and-depleted", limit: 1 });
  });

  it("returns null for a farm with no usable flock, rather than an archived one", async () => {
    expect(await resolveDefaultFlock([ARCHIVED])).toBeNull();
  });

  // Unbound account (signed out mid-flight): the storage helpers answer null
  // rather than reading another farm's memory.
  it("ignores remembered state when no account is bound", async () => {
    rememberFlockId("fl2");
    clearBoundAccount();

    expect(await resolveDefaultFlock([ACTIVE, ACTIVE_2])).toBe(ACTIVE);
  });
});
