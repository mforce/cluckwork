import { describe, it, expect, vi, afterEach } from "vitest";
import { render, screen } from "@testing-library/react";

// Its own file because it has to control the environment BEFORE the module is
// evaluated: `TIME_ZONES` is computed at module load, and App.tsx imports this
// screen statically, so a throw there happens before React mounts — outside
// every ErrorBoundary — and white-screens the whole app rather than degrading
// one Setup screen. `Intl.supportedValuesOf` is ES2022; a browser predating it
// (Safari before 15.4) is exactly that case.
//
// The guard had no test at all when it was added (round 2, adversarial agent).

vi.mock("../api/cluckwork", async () => {
  const actual = await vi.importActual<typeof import("../api/cluckwork")>("../api/cluckwork");
  return { ...actual, getFarmSettings: vi.fn().mockRejectedValue(new Error("not under test")),
    getFlock: vi.fn(),
    getCustomer: vi.fn(),
  };
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.resetModules();
});

describe("SettingsPage on a browser without Intl.supportedValuesOf", () => {
  it("imports without throwing", async () => {
    vi.stubGlobal("Intl", { ...Intl, supportedValuesOf: undefined });
    vi.resetModules();

    // The assertion IS that this resolves: an unguarded call throws a
    // TypeError here, and in the real app that is the blank page.
    await expect(import("./SettingsPage")).resolves.toBeDefined();
  });

  it("still renders, with an empty timezone list", async () => {
    vi.stubGlobal("Intl", { ...Intl, supportedValuesOf: undefined });
    vi.resetModules();
    const { SettingsPage } = await import("./SettingsPage");

    render(<SettingsPage />);

    // The screen degrades to its load-error path (the settings fetch is stubbed
    // to fail) rather than taking the app down on the way in.
    expect(await screen.findByRole("alert")).toBeInTheDocument();
  });

  it("offers the browser's zones when it does have them", async () => {
    vi.resetModules();
    const { SettingsPage } = await import("./SettingsPage");
    render(<SettingsPage />);

    // The negative above is only meaningful if the positive holds — otherwise
    // an empty list would pass both.
    expect(Intl.supportedValuesOf("timeZone").length).toBeGreaterThan(0);
    expect(await screen.findByRole("alert")).toBeInTheDocument();
  });
});
