// #1215 file split — the committed-selection lifecycle: commit, clear,
// exploration cancellation, the controlled-sync and outside-click effects and
// the unavailable-identity retry. Moved verbatim out of NamedEntityPicker.tsx.
// The selection generation token, the painted state, the committed text and
// the exact-read ref are created once in the component and passed in; the
// requestedId effect stays in NamedEntityPicker.tsx.

import { useCallback, useEffect, useRef } from "react";
import type { Dispatch, MutableRefObject, SetStateAction } from "react";
import type { EngineState, NamedEntity } from "./engineTypes";

interface UsePickerSelectionProps<T extends NamedEntity> {
  /** The engine's painted state; read via ref by the async paths. */
  stateRef: MutableRefObject<EngineState<T>>;
  setState: Dispatch<SetStateAction<EngineState<T>>>;
  setCommittedText: Dispatch<SetStateAction<string | null>>;
  /** SELECTION-transition generation (never gates discovery requests). */
  selectionGenRef: MutableRefObject<number>;
  /** The engine-owned debounce timer (shared with the discovery pipeline). */
  debounceRef: MutableRefObject<number | null>;
  /** The Autocomplete highlight, cleared on exploration cancellation. */
  highlightedIdRef: MutableRefObject<string | null>;
  fetchExactRef: MutableRefObject<((id: string) => Promise<T>) | undefined>;
  containerRef: MutableRefObject<HTMLDivElement | null>;
  id: string;
  open: boolean;
  onCommit?: (entity: T) => void;
  onClear?: () => void;
  onOutsideClick?: () => void;
  controlledCommitted?: T | null;
  controlledGeneration?: number;
}

export function usePickerSelection<T extends NamedEntity>({ stateRef, setState, setCommittedText, selectionGenRef, debounceRef, highlightedIdRef, fetchExactRef, containerRef, id, open, onCommit, onClear, onOutsideClick, controlledCommitted, controlledGeneration }: UsePickerSelectionProps<T>) {
  const onCommitRef = useRef(onCommit);
  onCommitRef.current = onCommit;
  const onClearRef = useRef(onClear);
  onClearRef.current = onClear;

  const commit = useCallback((entity: T) => {
    // Selection transition ONLY: the discovery window (rows, cursor, phase)
    // is retained for continued browsing, and a pending discovery keeps its
    // own generation — commit must not cancel or wedge it. The debounce is
    // still cancelled separately: a commit is a terminal selection intent for
    // the text being typed.
    selectionGenRef.current += 1;
    if (debounceRef.current !== null) {
      window.clearTimeout(debounceRef.current);
      debounceRef.current = null;
    }
    // Commit stores the entity AND sets the visible text to the entity's name.
    // The input value IS discovery.rawQuery; after commit, rawQuery ===
    // committedName so exploring is immediately false. The discovery window
    // (items, cursor, hasMore) is RETAINED for continued browsing.
    setCommittedText(entity.name);
    setState((prev) => ({
      ...prev,
      selection: { entity, requestedId: null, phase: "committed", transitionGeneration: selectionGenRef.current },
      discovery: {
        ...prev.discovery,
        rawQuery: entity.name,
      },
    }));
    // US2: fire the explicit commit signal (genuine user intent only).
    onCommitRef.current?.(entity);
  }, [setCommittedText, setState, debounceRef, selectionGenRef]);

  // US3 (T034/T038): Retry the FAILED exact-identity read. The unavailable
  // identity is remembered (selection.requestedId); the read is re-issued
  // under a fresh generation, and focus returns to the input on success.
  const retryUnavailable = useCallback(async () => {
    // Deliberately NOT gated on `disabled` (US3 remediation): this re-resolves
    // a FIXED external identity via the exact GET, not ordinary discovery or
    // selection — a picker disabled for other reasons (e.g. Water's
    // edit-locked capture picker) must still be able to recover a row-owned
    // id whose exact read failed.
    const reqId = stateRef.current.selection.requestedId;
    const inputEl = document.getElementById(id);
    if (!reqId || !fetchExactRef.current) return;
    const gen = ++selectionGenRef.current;
    try {
      const resolved = await fetchExactRef.current(reqId);
      if (selectionGenRef.current !== gen) return;
      // Sync the discovery window to the resolved identity: the input value IS
      // rawQuery, so committing without it leaves stale text painted and
      // `exploring` true (canSubmit false) after a successful exact read.
      // Only rawQuery/activeId move — rows, cursor and error belong to the
      // discovery generation, which this transition never supersedes.
      setCommittedText(resolved.name);
      setState((prev) => ({
        ...prev,
        selection: { entity: resolved, requestedId: null, phase: "committed", transitionGeneration: gen },
        discovery: { ...prev.discovery, rawQuery: resolved.name },
      }));
    } catch {
      if (selectionGenRef.current !== gen) return;
      setState((prev) => ({
        ...prev,
        selection: { entity: null, requestedId: reqId, phase: "unavailable", transitionGeneration: gen },
      }));
    }
    inputEl?.focus();
  }, [id, setCommittedText, setState, fetchExactRef, selectionGenRef, stateRef]);

  // US2: cancel exploration and restore committed/blank text — shared by
  // Escape (the picker root's `onKeyDown` in NamedEntityPicker.tsx). The
  // discovery window (items, cursor, hasMore, phase) is retained, never
  // wiped, and the discovery generation is untouched: an in-flight request
  // settles into the retained window under its own generation.
  const cancelExploration = useCallback(() => {
    if (debounceRef.current !== null) {
      window.clearTimeout(debounceRef.current);
      debounceRef.current = null;
    }
    // `highlightedIdRef` is set only by `onHighlightChange`; clear it here so
    // a reopen's first ArrowDown cannot read a stale `atEnd` (FR-032).
    highlightedIdRef.current = null;
    const selection = stateRef.current.selection;
    // A fixed requested-ID read belongs to the page's selection intent and
    // must still be allowed to settle.
    if (selection.phase !== "resolving" || selection.requestedId === null) {
      selectionGenRef.current += 1;
    }
    const committed = selection.entity;
    setState((prev) => ({
      ...prev,
      discovery: { ...prev.discovery, rawQuery: committed?.name ?? "" },
    }));
    setCommittedText(committed?.name ?? null);
  }, [setCommittedText, setState, debounceRef, highlightedIdRef, selectionGenRef, stateRef]);

  // US2: controlled committed state. When the page externally resets its
  // selection (deep link, URL change, dialog reset, create-then-hydrate),
  // it passes a new entity or null via `controlledCommitted` and bumps
  // `controlledGeneration`. The engine synchronizes its internal committed
  // state so that a later Escape or exploration cannot resurrect a stale ID.
  //
  // INITIAL VALUE: 0, not -1. The page's first controlled sync (mount-time
  // default, deep link, remembered) passes `controlledGeneration={1}` (or
  // higher). Starting at -1 would skip the FIRST sync when the page passes
  // 0 (a legitimate "no change yet" value) — but more importantly, the page
  // often passes 1 as its first bump, and the engine's `=== lastControlledGenRef.current`
  // check would then skip it if lastControlledGenRef started at 0 (a common
  // off-by-one). Starting at -1 makes the first sync (gen 0 or 1) always fire.
  // The page's own gen counter starts at 0 and bumps to 1 on its first
  // external reset, so the engine's -1 start guarantees the page's gen-1
  // sync is always observed.
  const lastControlledGenRef = useRef(-1);
  useEffect(() => {
    if (controlledGeneration === undefined || controlledGeneration === lastControlledGenRef.current) return;
    lastControlledGenRef.current = controlledGeneration;
    const entity = controlledCommitted ?? null;
    // A SELECTION transition: never bumps the discovery generation (an
    // in-flight replacement keeps its own generation and still settles) and
    // never wipes the discovery window.
    selectionGenRef.current += 1;
    setCommittedText(entity?.name ?? null);
    setState((prev) => {
      // If the picker is already showing results for this same entity, preserve
      // the discovery window (items, cursor, hasMore) — only sync the selection.
      // This avoids clearing a loaded list when the page re-hydrates the same
      // flock (e.g. the controlled-gen effect fires on mount after the picker
      // already discovered).
      const sameEntity = prev.selection.entity?.id === entity?.id;
      return {
        ...prev,
        selection: { entity, requestedId: null, phase: entity ? "committed" : "blank", transitionGeneration: prev.selection.transitionGeneration },
        discovery: sameEntity
          ? prev.discovery
          : { ...prev.discovery, rawQuery: entity?.name ?? "", items: [], hasMore: false, cursor: 0 },
      };
    });
    // Exact-identity validation: only when an exact read is provided AND the
    // entity is NOT already known (in the discovery window OR already the
    // committed selection from a genuine commit). A list-known or already-
    // committed entity is admitted as-is — no spurious GET on every page mount
    // or on every commit round-trip. The read is generation-owned; a failure
    // enters `unavailable` (committed stays null, canSubmit false) — never the
    // first result.
    // A full typed entity (with a real name) is admitted as-is: pages commit
    // the full entity from a genuine pick or from a page-owned source, and
    // re-validating every such commit with a GET would be a spurious round-
    // trip. Row-owned / late-sorting identity resolution (exact GET) is handled
    // by the `requestedId` effect in NamedEntityPicker.tsx (T038), which fires
    // only when the page explicitly names an ID it does not have the full
    // entity for.
    // The controlledCommitted path admits the entity as-is (US1/US2 behavior).
    // Row-owned / late-sorting identity resolution (exact GET) is handled by
    // the `requestedId` effect in NamedEntityPicker.tsx (T038), which fires
    // only when the page explicitly names an ID it does not have the full
    // entity for.
  }, [controlledCommitted, controlledGeneration, setState, setCommittedText, selectionGenRef]);

  // US2: outside-click cancellation. When the picker is open and the user
  // clicks outside the picker container: cancel exploration (restore committed
  // or blank text), cancel debounce, and close. The page's write-handler guard
  // (canSubmit) independently suppresses any same-interaction write.
  const onOutsideClickRef = useRef(onOutsideClick);
  onOutsideClickRef.current = onOutsideClick;
  useEffect(() => {
    if (!open) return;
    function handler(e: MouseEvent) {
      if (containerRef.current && !containerRef.current.contains(e.target as Node)) {
        // Restore committed/blank text before closing (finding 5: reopen must
        // not show an abandoned query).
        // Restore the committed/blank text and invoke the callback, but
        // PRESERVE the discovery window (items, cursor, hasMore, phase) and
        // do NOT bump the discovery generation: the page's onOutsideClick may
        // keep the picker open, and an always-open picker must retain its
        // options and remain usable. The debounce is still cancelled
        // separately (a stale typed replacement must not fire after restore).
        if (debounceRef.current !== null) {
          window.clearTimeout(debounceRef.current);
          debounceRef.current = null;
        }
        // Same stale-ref hazard as `cancelExploration`.
        highlightedIdRef.current = null;
        const selection = stateRef.current.selection;
        // Outside-click closes exploration, not the page-owned requested-ID
        // intent. Keep that exact read live while the picker is closed.
        if (selection.phase !== "resolving" || selection.requestedId === null) {
          selectionGenRef.current += 1;
        }
        const committed = selection.entity;
        setState((prev) => ({
          ...prev,
          discovery: {
            ...prev.discovery,
            rawQuery: committed?.name ?? "",
          },
        }));
        setCommittedText(committed?.name ?? null);
        onOutsideClickRef.current?.();
      }
    }
    document.addEventListener("mousedown", handler);
    return () => document.removeEventListener("mousedown", handler);
  }, [open, setState, setCommittedText, containerRef, debounceRef, highlightedIdRef, selectionGenRef, stateRef]);

  // US2: optional clear — commits blank. A selection transition only: the
  // discovery window is retained and its generation is untouched.
  const clearSelection = useCallback(() => {
    selectionGenRef.current += 1;
    if (debounceRef.current !== null) {
      window.clearTimeout(debounceRef.current);
      debounceRef.current = null;
    }
    setCommittedText(null);
    setState((prev) => ({
      ...prev,
      selection: { entity: null, requestedId: null, phase: "blank", transitionGeneration: selectionGenRef.current },
      discovery: { ...prev.discovery, rawQuery: "" },
    }));
    onClearRef.current?.();
  }, [setCommittedText, setState, debounceRef, selectionGenRef]);

  return { commit, retryUnavailable, cancelExploration, clearSelection };
}
