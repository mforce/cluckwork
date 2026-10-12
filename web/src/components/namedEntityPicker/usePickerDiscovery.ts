// #1215 file split — the discovery pipeline: debounced replacement, extension
// paging, retry and the suppressed eligibility-change effect. Bodies moved
// verbatim out of NamedEntityPicker.tsx; the shared refs (state, the debounce
// timer, the highlight, both generation tokens) are created once in the
// component and passed in, so every callback lists them explicitly. The open,
// focus and outside-click effects live in NamedEntityPicker.tsx, which owns
// those refs.

import { useCallback, useEffect, useRef } from "react";
import type { Dispatch, MutableRefObject, SetStateAction } from "react";
import type { EngineState, NamedEntity } from "./engineTypes";
import type { FlockEligibilityKey } from "./types";

// The adapter policy, fixed by the picker-ui contract: 50-row pages and the
// spec's exact 250 ms typing pause. Pages never configure either.
const PICKER_PAGE_SIZE = 50;
const PICKER_DEBOUNCE_MS = 250;

interface UsePickerDiscoveryProps<T extends NamedEntity> {
  /** The engine's painted state; read via ref by the extension/retry paths. */
  stateRef: MutableRefObject<EngineState<T>>;
  setState: Dispatch<SetStateAction<EngineState<T>>>;
  /** The engine-owned debounce timer (shared with the selection lifecycle). */
  debounceRef: MutableRefObject<number | null>;
  /** The Autocomplete highlight, cleared whenever the rows are replaced. */
  highlightedIdRef: MutableRefObject<string | null>;
  /** DISCOVERY generation: only discovery intents increment it. */
  discoveryGenRef: MutableRefObject<number>;
  /** Signals a same-commit eligibility change to the open effect. */
  pendingEligibilityKeyRef: MutableRefObject<FlockEligibilityKey | null>;
  fetchPage: (search: string | null, offset: number) => Promise<T[]>;
  disabled: boolean;
  id: string;
  open: boolean;
  eligibilityKey: FlockEligibilityKey | null;
}

export function usePickerDiscovery<T extends NamedEntity>({ stateRef, setState, debounceRef, highlightedIdRef, discoveryGenRef, pendingEligibilityKeyRef, fetchPage, disabled, id, open, eligibilityKey }: UsePickerDiscoveryProps<T>) {
  // The discovery pipeline. Every entry point claims the generation BEFORE it
  // awaits, and every continuation re-checks it — including the failure path,
  // because a superseded request's rejection is as stale as its response.
  const runReplacement = useCallback(async (query: string | null, gen: number) => {
    // Set "replacing" immediately before fetching. The generation check after
    // the await is the sole staleness guard: if discoveryGenRef.current !==
    // gen, this request was superseded and its response (or rejection) is
    // dropped.
    setState((prev) => ({
      ...prev,
      discovery: { ...prev.discovery, phase: "replacing", items: [], error: null, discoveryGeneration: gen },
    }));
    let page: T[];
    try {
      page = await fetchPage(query, 0);
    } catch (err) {
      if (discoveryGenRef.current !== gen) return;
      setState((prev) => ({
        ...prev,
        discovery: { ...prev.discovery, phase: "replacement-error", error: err instanceof Error ? err : new Error(String(err)) },
      }));
      return;
    }
    if (discoveryGenRef.current !== gen) return;
    setState((prev) => ({
      ...prev,
      discovery: {
        ...prev.discovery,
        items: page,
        cursor: page.length,
        hasMore: page.length === PICKER_PAGE_SIZE,
        phase: page.length === 0 ? "empty" : "ready",
        error: null,
        discoveryGeneration: gen,
      },
    }));
  }, [fetchPage, setState, discoveryGenRef]);

  // An extension fetches the window at `cursor` under the current query. The
  // cursor is captured at call time from the PAINTED state (stateRef) — the
  // caller (loadMore) runs only from event handlers, i.e. after the triggering
  // page has painted, so the painted cursor is exactly the next window's offset.
  const runExtension = useCallback(async (gen: number) => {
    const cursor = stateRef.current.discovery.cursor;
    const query = stateRef.current.discovery.normalizedQuery;
    setState((prev) => ({
      ...prev,
      discovery: { ...prev.discovery, phase: "extending", discoveryGeneration: gen },
    }));
    let page: T[];
    try {
      page = await fetchPage(query, cursor);
    } catch (err) {
      if (discoveryGenRef.current !== gen) return;
      setState((prev) => ({
        ...prev,
        discovery: {
          ...prev.discovery,
          phase: "extension-error",
          error: err instanceof Error ? err : new Error(String(err)),
        },
      }));
      return;
    }
    if (discoveryGenRef.current !== gen) return;
    setState((prev) => {
      const seen = new Set(prev.discovery.items.map((x) => x.id));
      const items = [...prev.discovery.items, ...page.filter((x) => !seen.has(x.id))];
      return {
        ...prev,
        discovery: {
          ...prev.discovery,
          items,
          // The cursor advances by the RAW server page length, not the
          // deduplicated appended count. A server that repeats an ID across
          // pages (a race, a re-index) must not shrink the window: the next
          // offset is cursor + page.length, always.
          cursor: cursor + page.length,
          hasMore: page.length === PICKER_PAGE_SIZE,
          phase: "ready",
          error: null,
          discoveryGeneration: gen,
        },
      };
    });
  }, [fetchPage, setState, stateRef, discoveryGenRef]);

  // Load more: an explicit user intent, so it claims a NEWER generation than
  // any pending replacement. One click = exactly one extension request
  // (data-model.md): a full page keeps `hasMore` true and the button stays
  // offered; a LATER explicit intent may fetch the final empty (or short)
  // page, which is what ends paging. The picker never probes automatically.
  const loadMore = useCallback(async () => {
    if (disabled) return;
    const gen = ++discoveryGenRef.current;
    await runExtension(gen);
  }, [runExtension, disabled, discoveryGenRef]);

  // US3 (T034): Retry re-issues the FAILED operation under the current
  // generation, then restores focus to the input. Replacement errors re-run
  // the replacement (offset 0, current query); extension errors re-run the
  // extension at the PAINTED cursor — which is unchanged while the error is
  // shown, so the retry targets exactly the failed window (same query, same
  // cursor). Focus restoration is the keyboard contract (picker-ui.md).
  const retry = useCallback(async () => {
    if (disabled) return;
    const d = stateRef.current.discovery;
    const inputEl = document.getElementById(id);
    const gen = ++discoveryGenRef.current;
    if (d.phase === "replacement-error") {
      await runReplacement(d.normalizedQuery, gen);
    } else if (d.phase === "extension-error") {
      await runExtension(gen);
    } else {
      return;
    }
    inputEl?.focus();
  }, [disabled, id, runReplacement, runExtension, stateRef, discoveryGenRef]);

  // Typing: the raw text hides the old rows IMMEDIATELY (FR-016), then the
  // exact 250 ms pause (FR-008) owns whether a replacement goes out. The
  // debounce timer is the newest-intent check: each keystroke cancels the
  // previous one, so only the final text ever requests.
  //
  // Takes the raw string directly (not a ChangeEvent): `Autocomplete`'s own
  // `onInputChange(event, value, reason)` is the only caller (#826 — the
  // combobox is `Autocomplete`'s, not a hand-rolled `<input onChange>`), and
  // it already hands over the string.
  const onQueryChange = useCallback((raw: string) => {
    if (disabled) return;
    const trimmed = raw.trim();
    const gen = ++discoveryGenRef.current;
    if (debounceRef.current !== null) window.clearTimeout(debounceRef.current);
    // Typing replaces `items`, so the highlighted option is gone with them.
    highlightedIdRef.current = null;
    setState((prev) => ({
      ...prev,
      discovery: {
        ...prev.discovery,
        rawQuery: raw,
        normalizedQuery: trimmed === "" ? null : trimmed,
        items: [],
        error: null,
        cursor: 0,
        hasMore: false,
        phase: "debouncing",
        discoveryGeneration: gen,
      },
    }));
    debounceRef.current = window.setTimeout(() => {
      debounceRef.current = null;
      void runReplacement(trimmed === "" ? null : trimmed, gen);
    }, PICKER_DEBOUNCE_MS);
  }, [runReplacement, disabled, setState, debounceRef, highlightedIdRef, discoveryGenRef]);

  // Eligibility change: while open, hide old rows immediately, invalidate old
  // work (bump the generation so in-flight requests become stale), and
  // re-discover under the new eligibility after the fixed 250 ms debounce.
  // While closed, synchronize prevEligibilityRef and the state's eligibilityKey
  // WITHOUT requesting — the next open will issue exactly one request under the
  // current eligibility (the open effect checks phase === "closed", which is
  // preserved, so no stale immediate + debounced double-fetch).
  //
  // DECLARED BEFORE the open effect: React runs effects in declaration order,
  // so when eligibility changes and open=true in the same commit, this effect
  // runs first and sets `pendingEligibilityKeyRef` (the open effect reads it
  // and skips). This prevents the open effect from launching a stale request
  // under the old key. After the debounce fires, the pending ref is cleared so
  // a subsequent reopen is not incorrectly skipped.
  const prevEligibilityRef = useRef(eligibilityKey);
  useEffect(() => {
    if (prevEligibilityRef.current === eligibilityKey) return;
    prevEligibilityRef.current = eligibilityKey;
    // Claim a DISCOVERY generation for this intent so in-flight discovery
    // work becomes stale and the state's discoveryGeneration is truthful.
    const gen = ++discoveryGenRef.current;
    if (!open) {
      // Closed: sync the key in state and reset the discovery window (items,
      // cursor, hasMore, phase → "closed") so that the next open issues exactly
      // one request under the CURRENT key.
      if (debounceRef.current !== null) {
        window.clearTimeout(debounceRef.current);
        debounceRef.current = null;
      }
      setState((prev) => ({
        ...prev,
        discovery: {
          ...prev.discovery,
          eligibilityKey,
          items: [],
          cursor: 0,
          hasMore: false,
          phase: "closed",
          discoveryGeneration: gen,
        },
      }));
      return;
    }
    // Open: hide rows, invalidate, and re-discover after the debounce.
    // Signal to the open effect (same commit) that the eligibility key just
    // changed — it should skip firing this commit.
    pendingEligibilityKeyRef.current = eligibilityKey;
    const query = stateRef.current.discovery.normalizedQuery;
    if (debounceRef.current !== null) {
      window.clearTimeout(debounceRef.current);
      debounceRef.current = null;
    }
    setState((prev) => ({
      ...prev,
      discovery: {
        ...prev.discovery,
        eligibilityKey,
        items: [],
        error: null,
        cursor: 0,
        hasMore: false,
        phase: "debouncing",
        discoveryGeneration: gen,
      },
    }));
    debounceRef.current = window.setTimeout(() => {
      debounceRef.current = null;
      // Clear the pending signal: the debounce has fired, so the open effect
      // should not skip any FUTURE reopen (where the phase may be "closed").
      pendingEligibilityKeyRef.current = null;
      void runReplacement(query, gen);
    }, PICKER_DEBOUNCE_MS);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [eligibilityKey, open]);

  return { retry, loadMore, onQueryChange, runReplacement };
}
