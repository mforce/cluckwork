import { describe, it, expect } from "vitest";
import { navGroups, tabEntries, type NavGroup } from "./nav";
import type { Role } from "../auth/claims";

// The per-role gates live only here (both navs render from this), so they are
// worth testing directly — a dropped condition would still pass every screen
// test that only renders Admin and ReadOnly.
const tabsFor = (role: Role, isAdmin: boolean) =>
  tabEntries(navGroups(role, isAdmin)).map((e) => e.label);
const reachable = (role: Role, isAdmin: boolean) =>
  new Set(navGroups(role, isAdmin).flatMap((g) => g.entries.map((e) => e.label)));

describe("navGroups role gates", () => {
  it("gives a producer (Admin/Manager/Worker) the production group and sales", () => {
    for (const [role, admin] of [["Admin", true], ["Manager", true], ["Worker", false]] as const) {
      const r = reachable(role, admin);
      expect(r.has("Daily entry")).toBe(true);
      expect(r.has("Sales")).toBe(true);
    }
  });

  it("denies Sales the production group but keeps sales", () => {
    const r = reachable("Sales", false);
    expect(r.has("Daily entry")).toBe(false); // cannot produce
    expect(r.has("Flocks")).toBe(false);
    expect(r.has("Sales")).toBe(true);
    expect(r.has("Stock")).toBe(true);
  });

  it("gives ReadOnly and Denied only what the API lets every principal read", () => {
    for (const role of ["ReadOnly", "Denied"] as const) {
      const r = reachable(role, false);
      expect(r.has("Daily entry")).toBe(false);
      expect(r.has("Sales")).toBe(false);
      expect(r.has("Customers")).toBe(false);
      // the reads everyone has
      expect(r.has("Stock")).toBe(true);
      expect(r.has("History")).toBe(true);
      expect(r.has("Dashboard")).toBe(true);
    }
  });

  it("shows Setup and Expenses only to admins", () => {
    expect(reachable("Admin", true).has("Grades")).toBe(true);
    expect(reachable("Admin", true).has("Expenses")).toBe(true);
    expect(reachable("Worker", false).has("Grades")).toBe(false);
    expect(reachable("Worker", false).has("Expenses")).toBe(false);
    // Manager is admin-tier but not Admin, so no Users
    expect(reachable("Manager", true).has("Users")).toBe(false);
    expect(reachable("Admin", true).has("Users")).toBe(true);
  });
});

describe("tabEntries", () => {
  it("promotes the four highest-priority destinations a producer reaches", () => {
    expect(tabsFor("Admin", true)).toEqual(["Daily entry", "Stock", "Sales", "History"]);
  });

  it("drops Daily entry for Sales and Daily+Sales for ReadOnly/Denied", () => {
    // Sales keeps its Sales tab; the fourth slot is Dashboard, because "/"
    // outranks "/reports" in TAB_PRIORITY and Sales fills four before reaching it.
    expect(tabsFor("Sales", false)).toEqual(["Stock", "Sales", "History", "Dashboard"]);
    // ReadOnly has no Sales, so History + Dashboard come earlier and Reports
    // makes the fourth.
    expect(tabsFor("ReadOnly", false)).toEqual(["Stock", "History", "Dashboard", "Reports"]);
    expect(tabsFor("Denied", false)).toEqual(["Stock", "History", "Dashboard", "Reports"]);
  });

  it("always returns exactly four, never a duplicate, for every role", () => {
    for (const [role, admin] of [
      ["Admin", true], ["Manager", true], ["Worker", false],
      ["Sales", false], ["ReadOnly", false], ["Denied", false],
    ] as const) {
      const tabs = tabsFor(role, admin);
      expect(tabs).toHaveLength(4);
      expect(new Set(tabs).size).toBe(4);
    }
  });

  it("backfills in group order when the priority list leaves it short", () => {
    // A synthetic nav whose reachable set barely overlaps TAB_PRIORITY: only
    // Stock is a priority route, so the other three must come from the backfill
    // loop — the path no real role exercises but the "never short" guarantee
    // depends on.
    const groups: NavGroup[] = [
      { label: "Setup", entries: [
        { to: "/grades", label: "Grades", Icon: (() => null) as never },
        { to: "/products", label: "Products", Icon: (() => null) as never },
        { to: "/stock", label: "Stock", Icon: (() => null) as never },
        { to: "/audit", label: "Audit", Icon: (() => null) as never },
        { to: "/export", label: "Export", Icon: (() => null) as never },
      ] },
    ];
    const tabs = tabEntries(groups).map((e) => e.label);
    // Stock first (it is in TAB_PRIORITY), then the rest in group order.
    expect(tabs).toEqual(["Stock", "Grades", "Products", "Audit"]);
    expect(tabs).toHaveLength(4);
  });

  it("returns everything when a group has fewer than four reachable entries", () => {
    const groups: NavGroup[] = [
      { label: "Overview", entries: [
        { to: "/", label: "Dashboard", Icon: (() => null) as never },
        { to: "/stock", label: "Stock", Icon: (() => null) as never },
      ] },
    ];
    expect(tabEntries(groups).map((e) => e.label)).toEqual(["Stock", "Dashboard"]);
  });
});
