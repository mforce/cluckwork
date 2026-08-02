// tools/simulation/ui/src/vitals.ts — Core Web Vitals collection for the canary
// (#386), measured in the page rather than inferred from the outside.
//
// ================== NO web-vitals DEPENDENCY, AND WHAT THAT COSTS ==================
//
// This uses `PerformanceObserver` directly. That keeps the canary dependency-free,
// and it is honest about the consequence, which is real:
//
//   * **LCP** — the last `largest-contentful-paint` entry OBSERVED WHEN THE
//     SAMPLE IS TAKEN. Not the finalised metric: real LCP is fixed at the first
//     user interaction or page hide, and this reads before either, so a later
//     larger paint would not be counted. In practice the read happens after the
//     screen's data is on the glass, which is past the candidate that matters —
//     but it is a provisional observation, not "exact" (PR #390 review corrected
//     that word).
//   * **CLS** — an APPROXIMATION. The real metric is the largest *session
//     window* (a 5s/1s-gap sliding window) of layout shifts. What is computed
//     here is the SUM of all shifts with `hadRecentInput === false` seen up to
//     the moment of the read. Larger than the session-window metric for the same
//     shifts, and bounded by when the sample is taken rather than by page hide.
//     Fine for "did the page get worse under load", wrong for quoting against
//     Google's thresholds.
//   * **INP** — NOT INP. The reported number is the LONGEST single interaction's
//     duration. Real INP is roughly the 98th percentile of interaction latencies,
//     which needs the interaction grouping `web-vitals` implements, so it is
//     labelled `longestInteractionMs` here and in the findings — never "INP".
//
// **"UPPER BOUND" IS ONLY TRUE IN ONE DIRECTION, AND THE FINDINGS DOC USED TO
// CLAIM BOTH.** Against the entries the observer has ALREADY delivered, summing
// every shift does over-report CLS's session window, and the longest interaction
// does over-report INP's percentile. But the sample is read mid-life, not at page
// hide, so a shift or an interaction occurring after the read is not in the set
// being bounded at all — and either figure can therefore come out BELOW the
// finalised standard metric. "Can only over-report" was wrong (PR #392 review
// round 3); the honest statement is that these bound what was observed, and
// observation stops at the read.
//
// Calling the approximations by their real names matters more than having a
// tidier table. A number labelled INP that is not INP will be compared against
// the 200ms threshold by somebody, eventually. The same applies to the word
// "exact": every figure here is an observation taken at a point in the page's
// life, and `web-vitals` with proper finalisation is the upgrade path if these
// ever need to be quoted as the standard metrics rather than compared run to run.
//
// The rest (TTFB, FCP, DOM-content-loaded, load) come from the Navigation Timing
// and Paint Timing entries and are exact.

import type { Page } from "@playwright/test";

export interface Vitals {
  /** The largest-contentful-paint candidate observed AT SAMPLE TIME — not the finalised LCP. */
  lcpMs: number | null;
  /** Sum of un-input-caused layout shifts observed by sample time. Bounds those entries, NOT the finalised CLS. */
  clsUpperBound: number | null;
  /** Longest interaction observed by sample time, ms. Bounds those entries, NOT the finalised INP. `null` when nothing was interacted with. */
  longestInteractionMs: number | null;
  /** How many interactions were observed. 0 means `longestInteractionMs` is null because nothing happened. */
  interactionCount: number;
  /** Time to first byte for the navigation, ms. From Navigation Timing; exact. */
  ttfbMs: number | null;
  /** First Contentful Paint, ms. From Paint Timing; exact. */
  fcpMs: number | null;
  domContentLoadedMs: number | null;
  loadMs: number | null;
}

/**
 * Install the collectors. MUST be called before the navigation being measured —
 * `addInitScript` runs before any page script, which is the only way to catch
 * the LCP and layout-shift entries that occur during the initial paint.
 */
export async function installVitals(page: Page): Promise<void> {
  await page.addInitScript(() => {
    interface VitalsBucket {
      lcp: number | null;
      cls: number;
      longestInteraction: number;
      // Counted so "no interaction happened" can be reported as null rather than
      // as 0ms. A 0 here reads as "instantaneous", which is the opposite of
      // "never measured", and the canary genuinely does not interact with every
      // screen.
      interactionCount: number;
    }
    const bucket: VitalsBucket = { lcp: null, cls: 0, longestInteraction: 0, interactionCount: 0 };
    (globalThis as unknown as { __cwv: VitalsBucket }).__cwv = bucket;

    // `buffered: true` on every observer so entries emitted before this script
    // finished registering are still delivered.
    try {
      new PerformanceObserver((list) => {
        for (const entry of list.getEntries()) bucket.lcp = entry.startTime;
      }).observe({ type: "largest-contentful-paint", buffered: true });
    } catch { /* not supported — reported as null rather than as zero */ }

    try {
      new PerformanceObserver((list) => {
        for (const entry of list.getEntries()) {
          const shift = entry as PerformanceEntry & { value: number; hadRecentInput: boolean };
          // A shift the user caused (typing, clicking) is not a layout
          // instability — excluding it is part of the metric's definition, not
          // a convenience.
          if (!shift.hadRecentInput) bucket.cls += shift.value;
        }
      }).observe({ type: "layout-shift", buffered: true });
    } catch { /* not supported */ }

    try {
      new PerformanceObserver((list) => {
        for (const entry of list.getEntries()) {
          bucket.interactionCount += 1;
          if (entry.duration > bucket.longestInteraction) {
            bucket.longestInteraction = entry.duration;
          }
        }
        // `durationThreshold: 0` would report every event; 16ms (one frame) is
        // the smallest interaction that could plausibly be felt.
      }).observe({ type: "event", buffered: true, durationThreshold: 16 } as PerformanceObserverInit);
    } catch { /* not supported */ }
  });
}

/** Read what the collectors gathered, plus the exact navigation/paint timings. */
export async function readVitals(page: Page): Promise<Vitals> {
  return page.evaluate(() => {
    const bucket = (globalThis as unknown as {
      __cwv?: {
        lcp: number | null;
        cls: number;
        longestInteraction: number;
        interactionCount: number;
      };
    }).__cwv;

    const nav = performance.getEntriesByType("navigation")[0] as PerformanceNavigationTiming | undefined;
    const fcp = performance
      .getEntriesByType("paint")
      .find((p) => p.name === "first-contentful-paint");

    const round = (n: number | null | undefined) =>
      n === null || n === undefined ? null : Math.round(n * 10) / 10;

    return {
      lcpMs: round(bucket?.lcp ?? null),
      // 0 shifts is a real, good result — distinguish it from "not observed".
      clsUpperBound: bucket ? Math.round(bucket.cls * 10000) / 10000 : null,
      // null, not 0, when nothing was interacted with — see interactionCount.
      longestInteractionMs:
        bucket && bucket.interactionCount > 0 ? round(bucket.longestInteraction) : null,
      interactionCount: bucket?.interactionCount ?? 0,
      ttfbMs: round(nav ? nav.responseStart - nav.requestStart : null),
      fcpMs: round(fcp?.startTime ?? null),
      domContentLoadedMs: round(nav?.domContentLoadedEventEnd ?? null),
      loadMs: round(nav?.loadEventEnd ?? null),
    };
  });
}

export interface ScreenSample extends Vitals {
  screen: string;
  /**
   * Every screen THIS canary build intended to sample, written into every file.
   *
   * The completeness check lives in the DATA rather than in a list maintained
   * inside run-baseline.sh (PR #392 review round 3). A renderer-side constant is
   * a second copy of `SCREENS` that nothing keeps in step: add a screen to the
   * canary and the renderer calls it "unexpected"; remove one and the renderer
   * reports a missing screen forever. Carrying the manifest alongside the sample
   * makes the two impossible to disagree, and lets a stale file from an older
   * build be recognised as such instead of silently counting as coverage.
   */
  expectedScreens: string[];
  /** Wall-clock from navigation start to the screen's own "I am ready" assertion passing. */
  usableInMs: number;
  underLoad: boolean;
}

/** One markdown table of every sample, for the findings doc. */
export function toMarkdownTable(samples: ScreenSample[]): string {
  if (samples.length === 0) return "_No canary samples were collected._";
  const fmt = (n: number | null) => (n === null ? "—" : String(n));
  const rows = samples.map(
    (s) =>
      `| ${s.screen} | ${s.underLoad ? "yes" : "no"} | ${fmt(s.ttfbMs)} | ${fmt(s.fcpMs)} `
      + `| ${fmt(s.lcpMs)} | ${fmt(s.clsUpperBound)} | ${fmt(s.longestInteractionMs)} | ${s.usableInMs} |`,
  );
  return [
    "| Screen | Under k6 load | TTFB ms | FCP ms | LCP ms | CLS (upper bound) | Longest interaction ms | Usable in ms |",
    "| --- | --- | --- | --- | --- | --- | --- | --- |",
    ...rows,
  ].join("\n");
}
