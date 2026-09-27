import { afterEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import { useTheme } from "@mui/material/styles";
import Chip from "@mui/material/Chip";
import { FarmThemeProvider } from "./FarmThemeProvider";
import { syncThemeColorMeta } from "./metaThemeColor";
import { stubMatchMedia } from "../test/matchMedia";
import { MD_UP_QUERY } from "../lib/breakpoints";

// `stubMatchMedia` shares one `matches` value across every query string,
// which conflates watchDeviceTheme's `(prefers-color-scheme: dark)` listener
// with the layout listener below — a mutation that deletes the layout
// listener entirely still passes a test built on the shared stub, because
// watchDeviceTheme's own listener picks up the slack. This one keys state by
// query so only MD_UP_QUERY's listener fires.
function stubMatchMediaPerQuery(initial: Record<string, boolean>) {
  const state = { ...initial };
  const listeners = new Map<string, Set<(e: { matches: boolean }) => void>>();
  const mock = vi.fn((query: string) => ({
    get matches() { return state[query] ?? false; },
    media: query,
    addEventListener: (_type: string, cb: (e: { matches: boolean }) => void) => {
      if (!listeners.has(query)) listeners.set(query, new Set());
      listeners.get(query)!.add(cb);
    },
    removeEventListener: (_type: string, cb: (e: { matches: boolean }) => void) => {
      listeners.get(query)?.delete(cb);
    },
    addListener: () => {},
    removeListener: () => {},
    dispatchEvent: vi.fn(),
  }));
  vi.stubGlobal("matchMedia", mock);
  return {
    triggerChange: (query: string, matches: boolean) => {
      state[query] = matches;
      listeners.get(query)?.forEach((cb) => cb({ matches }));
    },
  };
}

// #974 — which colour `--lavender` resolves to per brand/mode is pinned by
// metaThemeColor.test.ts against the real stylesheet; this file only has
// jsdom's fallback tokens (constant regardless of data-theme/data-brand), so
// it can prove WIRING (does the provider call the sync on mount and again
// after an outside-React switch) but not the resolved colour itself.
vi.mock("./metaThemeColor", () => ({ syncThemeColorMeta: vi.fn() }));

function Probe() {
  const theme = useTheme();
  return <output>{theme.palette.mode}</output>;
}

afterEach(() => {
  delete document.documentElement.dataset.theme;
  delete document.documentElement.dataset.brand;
});

describe("FarmThemeProvider (#674)", () => {
  it("hands MUI the document's current mode on mount", () => {
    document.documentElement.dataset.theme = "dark";
    render(<FarmThemeProvider><Probe /></FarmThemeProvider>);
    expect(screen.getByRole("status")).toHaveTextContent("dark");
  });

  it("follows a data-theme change made outside React", async () => {
    render(<FarmThemeProvider><Probe /></FarmThemeProvider>);
    expect(screen.getByRole("status")).toHaveTextContent("light");
    document.documentElement.dataset.theme = "dark";
    await waitFor(() => expect(screen.getByRole("status")).toHaveTextContent("dark"));
    document.documentElement.dataset.theme = "light";
    await waitFor(() => expect(screen.getByRole("status")).toHaveTextContent("light"));
  });

  it("syncs meta[name=theme-color] on mount and again after a data-theme change outside React (#974)", async () => {
    const spy = vi.mocked(syncThemeColorMeta);
    spy.mockClear();

    render(<FarmThemeProvider><Probe /></FarmThemeProvider>);
    await waitFor(() => expect(spy).toHaveBeenCalled());
    const callsOnMount = spy.mock.calls.length;

    document.documentElement.dataset.theme = "dark";
    await waitFor(() => expect(spy.mock.calls.length).toBeGreaterThan(callsOnMount));
  });

  // #976 round 1 — the provider wires watchDeviceTheme into the SAME
  // observer that already drives the mount/switch cadence above: an OS
  // change writes data-theme, which the observer catches, so both MUI's
  // mode and the meta colour follow with no separate recompute path.
  it("follows a live OS scheme change when nothing is explicitly saved", async () => {
    const media = stubMatchMedia(false);
    const spy = vi.mocked(syncThemeColorMeta);
    spy.mockClear();

    render(<FarmThemeProvider><Probe /></FarmThemeProvider>);
    await waitFor(() => expect(spy).toHaveBeenCalled());

    media.triggerChange(true);
    await waitFor(() => expect(screen.getByRole("status")).toHaveTextContent("dark"));
    expect(document.documentElement.dataset.theme).toBe("dark");
  });

  it("re-syncs meta[name=theme-color] when the layout crosses MD_UP_QUERY", async () => {
    const media = stubMatchMediaPerQuery({
      "(prefers-color-scheme: dark)": false,
      [MD_UP_QUERY]: true,
    });
    const spy = vi.mocked(syncThemeColorMeta);
    spy.mockClear();

    render(<FarmThemeProvider><Probe /></FarmThemeProvider>);
    await waitFor(() => expect(spy).toHaveBeenCalled());
    const callsOnMount = spy.mock.calls.length;

    media.triggerChange(MD_UP_QUERY, false); // crossed from desktop to phone width
    await waitFor(() => expect(spy.mock.calls.length).toBeGreaterThan(callsOnMount));
  });

  // #873 — this file carries NO csp-nonce meta, which is the Vite dev server's
  // document. The cache must then carry no nonce at all rather than an empty or
  // literal one: `nonce=""` is a value the browser compares against the policy
  // and rejects, so a fallback here would break dev to look tidy in production.
  // The meta-present direction is FarmThemeProvider.nonce.test.tsx.
  it("leaves the nonce off when the document carries no meta", () => {
    expect(document.querySelector('meta[name="csp-nonce"]')).toBeNull();

    render(<FarmThemeProvider><Chip label="probe" color="primary" /></FarmThemeProvider>);

    const styles = [...document.querySelectorAll<HTMLStyleElement>("style[data-emotion]")];
    expect(styles.length).toBeGreaterThan(0);
    for (const style of styles) expect(style.hasAttribute("nonce")).toBe(false);
  });
});
