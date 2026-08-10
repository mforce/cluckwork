// tools/simulation/ui/src/ax.ts — read Chromium's real accessibility tree.
//
// ================== WHY CDP, AND NOT PLAYWRIGHT'S OWN a11y API ==================
//
// **Playwright's accessibility surface does not model `inert`.** Established by
// probe against a standalone page (a `<p aria-live>` and a `<button>` inside a
// wrapper flipped to `inert`), not assumed:
//
//   * `locator.ariaSnapshot()` still lists the paragraph and the button.
//   * `isVisible()` and `isEnabled()` both still return `true`.
//   * Only `click()` notices, and only by timing out on actionability.
//   * CDP `Accessibility.getFullAXTree` is the one that agrees with the spec:
//     the node is absent from the tree entirely.
//
// So a spec asking "is this out of the accessibility tree while a dialog is
// open?" through `getByRole` or `ariaSnapshot` would pass whether or not the
// app inerts anything — the same shape of tautology as asserting the SW
// navigation denylist with `fetch()` instead of a navigation (pwa.spec.ts).
// Anything reasoning about `inert` must come through here.
//
// ================== WHAT ABSENCE MEANS, AND HOW NOT TO BE FOOLED BY IT ==================
//
// A node missing from the tree is the signal these specs want, and it is also
// what a mistyped selector, an unmounted component, or a stale snapshot look
// like. `axNode` therefore reports `inDom` separately from `inTree`, so a spec
// can assert "the element EXISTS and is out of the tree" rather than the much
// weaker "I could not find it". Pair every absence assertion with a control
// node that must still be present — and read the two through `nodes()`, which
// resolves them against ONE tree snapshot. `node()` takes its own snapshot per
// call, so a target and a control read through it separately are several CDP
// round trips apart and cannot rule out a tree that changed in between.
//
// Two Chromium behaviours this file depends on, both measured on 151 rather
// than assumed, and neither guaranteed by any spec (the CDP Accessibility
// domain is experimental):
//
//   * An `inert` subtree's nodes are ABSENT from the tree, not present with
//     `ignored: true`. So is an `aria-hidden` element — checked, because a
//     reviewer's proposed "aria-hidden slips through `inTree`" regression
//     turned out not to reproduce.
//   * `aria-live="off"` yields NO live property at all, not `live: "off"` —
//     which is also what a non-live element yields, so any assertion about it
//     needs a contrasting control.
//
// Test 3 of `a11y-live-regions.spec.ts` records the browser build it saw, so a
// future failure reads as "recorded on X, failing on Y".

import type { Page } from "@playwright/test";

export interface AxNode {
  /** The element exists in the DOM. False means the selector matched nothing. */
  inDom: boolean;
  /** The element has a node in Chromium's accessibility tree. */
  inTree: boolean;
  /**
   * Chromium's own "present but ignored" flag, for a node that is in the tree
   * and excluded from it (e.g. `aria-hidden`). An `inert` subtree does not get
   * this far — its nodes are absent altogether — so this is here to tell the
   * two exclusions apart rather than to conflate them.
   */
  ignored: boolean | null;
  ignoredReasons: string[];
  role: string | null;
  /** Computed live-region politeness ("off" | "polite" | "assertive"), as the browser resolves it. */
  live: string | null;
  atomic: boolean | null;
  /** The element's text, read from the DOM — the tree carries it on a child StaticText node. */
  text: string | null;
}

export interface AxReader {
  /** Resolve one selector against a fresh tree snapshot. */
  node(selector: string): Promise<AxNode>;
  /**
   * Resolve several selectors against a SINGLE tree snapshot, in the order
   * given. Use this whenever an absence and its control have to describe the
   * same moment — which is every time one is used to validate the other.
   */
  nodes(selectors: readonly string[]): Promise<AxNode[]>;
}

/**
 * Attach an accessibility reader to a page. One CDP session per page; call once
 * per test and reuse. Each `node()` call takes a FRESH tree snapshot, because
 * the property under test is exactly how the tree changes as dialogs open.
 */
export async function attachAx(page: Page): Promise<AxReader> {
  const cdp = await page.context().newCDPSession(page);
  await cdp.send("DOM.enable");
  await cdp.send("Accessibility.enable");

  async function backendIdFor(selector: string): Promise<number | null> {
    // The document must be re-fetched each time: `DOM.querySelector` takes a
    // nodeId, and ids from an earlier document are stale after a navigation.
    const doc = (await cdp.send("DOM.getDocument", { depth: -1 })) as {
      root: { nodeId: number };
    };
    const { nodeId } = (await cdp.send("DOM.querySelector", {
      nodeId: doc.root.nodeId,
      selector,
    })) as { nodeId: number };
    // 0 is CDP's "no match" — not an error, and not the same as "matched but
    // out of the tree", which is the distinction these specs turn on.
    if (!nodeId) return null;

    try {
      const { node } = (await cdp.send("DOM.describeNode", { nodeId })) as {
        node: { backendNodeId: number };
      };
      return node.backendNodeId;
    } catch (cause) {
      // The element was removed between the query and the describe. Do NOT
      // fall through to "absent": absence is the very thing some assertions
      // here are looking for, so swallowing this would turn an instrument
      // failure into a passing test. Fail loudly and name the selector.
      throw new Error(
        `ax: "${selector}" was resolved and then vanished before it could be described — the `
          + `page changed mid-read, so no conclusion about the accessibility tree is available.`,
        { cause },
      );
    }
  }

  async function read(selectors: readonly string[]): Promise<AxNode[]> {
    const backendIds = [];
    for (const selector of selectors) backendIds.push(await backendIdFor(selector));

    // ONE snapshot for the whole batch — that is the point of `nodes()`.
    const { nodes } = (await cdp.send("Accessibility.getFullAXTree")) as { nodes: AxRawNode[] };

    const out: AxNode[] = [];
    for (const [i, selector] of selectors.entries()) {
      const absent: AxNode = {
        inDom: false,
        inTree: false,
        ignored: null,
        ignoredReasons: [],
        role: null,
        live: null,
        atomic: null,
        text: null,
      };
      const backendId = backendIds[i];
      if (backendId === null || backendId === undefined) {
        out.push(absent);
        continue;
      }

      const text = await page.locator(selector).first().textContent();
      const hit = nodes.find((n) => n.backendDOMNodeId === backendId);
      if (!hit) {
        out.push({ ...absent, inDom: true, text });
        continue;
      }

      const prop = (name: string) => hit.properties?.find((p) => p.name === name)?.value?.value;
      out.push({
        inDom: true,
        inTree: true,
        ignored: hit.ignored ?? false,
        ignoredReasons: (hit.ignoredReasons ?? []).map((r) => r.name),
        role: (hit.role?.value as string) ?? null,
        live: (prop("live") as string) ?? null,
        atomic: (prop("atomic") as boolean) ?? null,
        text,
      });
    }
    return out;
  }

  return {
    async node(selector: string): Promise<AxNode> {
      return (await read([selector]))[0]!;
    },
    nodes: read,
  };
}

interface AxRawNode {
  backendDOMNodeId?: number;
  ignored?: boolean;
  ignoredReasons?: { name: string }[];
  role?: { value?: unknown };
  properties?: { name: string; value?: { value?: unknown } }[];
}
