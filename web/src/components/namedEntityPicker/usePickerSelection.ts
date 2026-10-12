// #1215 file split — the committed-selection lifecycle callbacks: commit,
// clear, exploration cancellation and the unavailable-identity retry. Bodies
// moved verbatim out of NamedEntityPicker.tsx; the shared refs (state, the
// committed text, the generation token, the exact-read ref) are created once
// in the component and passed in, so every callback lists them explicitly.
// The controlled-sync, requestedId and outside-click effects live in
// NamedEntityPicker.tsx, which owns those refs.

import { useCallback, useRef } from "react";
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
  id: string;
  onCommit?: (entity: T) => void;
  onClear?: () => void;
}

export function usePickerSelection<T extends NamedEntity>({ stateRef, setState, setCommittedText, selectionGenRef, debounceRef, highlightedIdRef, fetchExactRef, id, onCommit, onClear }: UsePickerSelectionProps<T>) {
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
  // Escape (`Autocomplete`'s `onClose` with reason "escape", in NamedEntityPicker.tsx). The
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
