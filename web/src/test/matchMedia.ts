import { vi } from "vitest";

// jsdom has no `matchMedia` (confirmed directly: FarmThemeProvider.render.
// test.tsx, and BottomNav's own effect guards on `typeof window.matchMedia
// !== "function"`). Left unstubbed, MUI's `useMediaQuery` falls back to its
// `defaultMatches` (false — the "below the breakpoint" branch) and a plain
// `window.matchMedia(...)` call throws. A test whose assertion depends on
// which side of a breakpoint the component resolves calls this first.
//
// `matches` is a shared mutable getter, not a value frozen at stub time: a
// caller that also wants to simulate a LIVE breakpoint crossing (a resize
// while a component is mounted, not just its initial render) uses the
// returned `triggerChange`, which flips it and fires every listener any
// `matchMedia(...)` call registered — the same list a real MediaQueryList's
// `change` event would reach.
export function stubMatchMedia(initialMatches: boolean) {
  const state = { matches: initialMatches };
  const listeners = new Set<(e: MediaQueryListEvent) => void>();
  const mock = vi.fn((query: string) => ({
    get matches() { return state.matches; },
    media: query,
    onchange: null,
    addEventListener: (_type: string, cb: (e: MediaQueryListEvent) => void) => listeners.add(cb),
    removeEventListener: (_type: string, cb: (e: MediaQueryListEvent) => void) => listeners.delete(cb),
    addListener: (cb: (e: MediaQueryListEvent) => void) => listeners.add(cb),
    removeListener: (cb: (e: MediaQueryListEvent) => void) => listeners.delete(cb),
    dispatchEvent: vi.fn(),
  }));
  vi.stubGlobal("matchMedia", mock);
  return {
    // The mock itself, so a test can assert WHICH query string a component
    // asked for (`expect(matchMedia).toHaveBeenCalledWith(MD_UP_QUERY)`) —
    // the part a fixed `matches` value alone cannot pin, since any query
    // string gets the same stubbed answer.
    matchMedia: mock,
    triggerChange: (matches: boolean) => {
      state.matches = matches;
      listeners.forEach((cb) => cb({ matches } as MediaQueryListEvent));
    },
  };
}
