import { describe, it, expect } from "vitest";
import { navGroups, tabEntries, type NavGroup } from "./nav";
import type { Role } from "../auth/claims";

// The per-role gates live only here (both navs render from this), so they are
// worth testing directly — a dropped condition would still pass every screen
// test that only renders Admin and ReadOnly.
//
// Assertions key off `labelKey` (#182, Task 7) rather than English text: nav
// items no longer carry a hardcoded label (navGroups/tabEntries are pure
// functions that cannot call useTranslation), so labelKey — resolved against
// the `nav` catalog only at the AppLayout/BottomNav render sites — is this
// data's real identity.
const tabsFor = (role: Role, isAdmin: boolean) =>
  tabEntries(navGroups(role, isAdmin)).map((e) => e.labelKey);
const reachable = (role: Role, isAdmin: boolean) =>
  new Set(navGroups(role, isAdmin).flatMap((g) => g.entries.map((e) => e.labelKey)));

describe("navGroups role gates", () => {
  it("gives a producer (Admin/Manager/Worker) the production group and sales", () => {
    for (const [role, admin] of [["Admin", true], ["Manager", true], ["Worker", false]] as const) {
      const r = reachable(role, admin);
      expect(r.has("dailyEntry")).toBe(true);
      expect(r.has("sales")).toBe(true);
    }
  });

  it("denies Sales the production group but keeps sales", () => {
    const r = reachable("Sales", false);
    expect(r.has("dailyEntry")).toBe(false); // cannot produce
    expect(r.has("flocks")).toBe(false);
    expect(r.has("sales")).toBe(true);
    expect(r.has("stock")).toBe(true);
  });

  it("gives ReadOnly and Denied only what the API lets every principal read", () => {
    for (const role of ["ReadOnly", "Denied"] as const) {
      const r = reachable(role, false);
      expect(r.has("dailyEntry")).toBe(false);
      expect(r.has("sales")).toBe(false);
      expect(r.has("customers")).toBe(false);
      // the reads everyone has
      expect(r.has("stock")).toBe(true);
      expect(r.has("history")).toBe(true);
      expect(r.has("dashboard")).toBe(true);
    }
  });

  it("shows Setup and Expenses only to admins", () => {
    expect(reachable("Admin", true).has("grades")).toBe(true);
    expect(reachable("Admin", true).has("expenses")).toBe(true);
    expect(reachable("Worker", false).has("grades")).toBe(false);
    expect(reachable("Worker", false).has("expenses")).toBe(false);
    // Manager is admin-tier but not Admin, so no Users
    expect(reachable("Manager", true).has("users")).toBe(false);
    expect(reachable("Admin", true).has("users")).toBe(true);
  });

  it("offers Farm settings to the admin tier — Manager included, unlike Users (#123)", () => {
    // The API gates /account/settings on AdminOnly (Owner OR Manager), so the
    // nav must not be narrower than that or a Manager loses a screen they can
    // actually use.
    expect(reachable("Admin", true).has("farmSettings")).toBe(true);
    expect(reachable("Manager", true).has("farmSettings")).toBe(true);
    for (const role of ["Worker", "Sales", "ReadOnly", "Denied"] as const)
      expect(reachable(role, false).has("farmSettings")).toBe(false);
  });
});

describe("tabEntries", () => {
  it("promotes the four highest-priority destinations a producer reaches", () => {
    expect(tabsFor("Admin", true)).toEqual(["dailyEntry", "stock", "sales", "history"]);
  });

  it("drops Daily entry for Sales and Daily+Sales for ReadOnly/Denied", () => {
    // Sales keeps its Sales tab; the fourth slot is Dashboard, because "/"
    // outranks "/reports" in TAB_PRIORITY and Sales fills four before reaching it.
    expect(tabsFor("Sales", false)).toEqual(["stock", "sales", "history", "dashboard"]);
    // ReadOnly has no Sales, so History + Dashboard come earlier and Reports
    // makes the fourth.
    expect(tabsFor("ReadOnly", false)).toEqual(["stock", "history", "dashboard", "reports"]);
    expect(tabsFor("Denied", false)).toEqual(["stock", "history", "dashboard", "reports"]);
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
      { labelKey: "groupSetup", entries: [
        { to: "/grades", labelKey: "grades", Icon: (() => null) as never },
        { to: "/products", labelKey: "products", Icon: (() => null) as never },
        { to: "/stock", labelKey: "stock", Icon: (() => null) as never },
        { to: "/audit", labelKey: "audit", Icon: (() => null) as never },
        { to: "/export", labelKey: "export", Icon: (() => null) as never },
      ] },
    ];
    const tabs = tabEntries(groups).map((e) => e.labelKey);
    // Stock first (it is in TAB_PRIORITY), then the rest in group order.
    expect(tabs).toEqual(["stock", "grades", "products", "audit"]);
    expect(tabs).toHaveLength(4);
  });

  it("returns everything when a group has fewer than four reachable entries", () => {
    const groups: NavGroup[] = [
      { labelKey: "groupOverview", entries: [
        { to: "/", labelKey: "dashboard", Icon: (() => null) as never },
        { to: "/stock", labelKey: "stock", Icon: (() => null) as never },
      ] },
    ];
    expect(tabEntries(groups).map((e) => e.labelKey)).toEqual(["stock", "dashboard"]);
  });
});
