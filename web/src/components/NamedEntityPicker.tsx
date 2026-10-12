// #512 — the shared named-entity picker: transport/selection/snapshot
// contracts plus the one async discovery+commit engine they describe.
//
// THIS FILE IS THE ENGINE. Pages never import it: they consume the typed
// `FlockPicker` / `CustomerPicker` adapters (FlockPicker.tsx /
// CustomerPicker.tsx), and the engine itself is feature-local. Nothing
// outside this feature should import from this module, and it must never grow
// a generic catalog/entity-link API — the spec (FR-053) forbids a shared
// framework beyond the two typed adapters.
//
// COMPOSITION ROOT: owns the engine's refs/setters and every effect in base order; hooks hold callbacks; internals: ./namedEntityPicker/.

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import type { KeyboardEvent } from "react";
import { useTranslation } from "react-i18next";
import { FLOCK_ELIGIBILITIES } from "./namedEntityPicker/types";
import type { FlockEligibilityKey, NamedEntityPage, DiscoveryPhase, DiscoveryState, SelectionPhase, PickerSelection, PickerSnapshot } from "./namedEntityPicker/types";
import { initialState } from "./namedEntityPicker/engineTypes";
import type { EngineProps, EngineState, NamedEntity } from "./namedEntityPicker/engineTypes";
import { usePickerDiscovery } from "./namedEntityPicker/usePickerDiscovery";
import { usePickerSelection } from "./namedEntityPicker/usePickerSelection";
import { usePickerPaper } from "./namedEntityPicker/PickerPaper";
import { PickerClosedState } from "./namedEntityPicker/PickerClosedState";
import { PickerOpenState } from "./namedEntityPicker/PickerOpenState";

export { FLOCK_ELIGIBILITIES };
export type { FlockEligibilityKey, NamedEntityPage, DiscoveryPhase, DiscoveryState, SelectionPhase, PickerSelection, PickerSnapshot };

/**
 * The one async picker engine. US1 scope: discovery (debounce, replacement,
 * extension, dedupe, raw-count cursor, final-empty termination), commit by
 * pointer/Enter, Arrow activation, committed-label retention (FR-018),
 * eligibility-change rediscovery, and disabled non-interactivity.
 * Selection-transition generations, unavailable states, Escape/clear, Retry,
 * and the full ARIA write-safety surface are US2/US3 (T026–T034).
 *
 * State discipline: a single `EngineState` object, updated ONLY through
 * functional `setState((prev) => …)` updaters that build on `prev` — never on
 * a ref snapshot of the painted state. The generation token (genRef) is the
 * only cross-await authority: every continuation re-checks it after every
 * await (success AND failure) before touching state.
 */

// --- The engine (T014) -------------------------------------------------------

export function NamedEntityPickerEngine<T extends NamedEntity>({ id, label, trigger, fetchPage, fetchExact, eligibilityKey, required = false, disabled = false, open = false, onSnapshot, onEscape, onOutsideClick, controlledCommitted, controlledGeneration, requestedId, onCommit, onClear, pinnedChoice }: EngineProps<T>) {
  const { t } = useTranslation("namedEntityPicker");
  const [state, setState] = useState<EngineState<T>>(() => initialState(eligibilityKey));
  const stateRef = useRef(state);
  stateRef.current = state;
  // DISCOVERY generation: owns replacement/extension requests and every
  // phase/rows/cursor they commit. Only discovery intents (typing, eligibility
  // change, open/close, Load more, discovery Retry) increment it. A superseded
  // discovery's continuation (success AND failure) re-checks it after the
  // await — but a SELECTION transition never touches it, so committing an
  // option, clearing, Escape, an outside click, a controlled sync or a
  // requestedId/exact-Retry transition can never drop or wedge a discovery
  // that is still the newest discovery intent.
  const discoveryGenRef = useRef(0);
  // SELECTION-transition generation: owns the committed-selection lifecycle
  // (transitionGeneration, controlled sync, requestedId resolution, exact
  // Retry). It never gates discovery requests: discovery completions are
  // checked against discoveryGenRef only.
  const selectionGenRef = useRef(0);
  // The debounce timer is owned by the engine, not by `commit`, so commit can
  // cancel a pending replacement without a forward-reference.
  const debounceRef = useRef<number | null>(null);
  useEffect(() => () => {
    if (debounceRef.current !== null) {
      window.clearTimeout(debounceRef.current);
      debounceRef.current = null;
    }
  }, []);
  // The committed label, retained independently of the editable text (FR-018).
  // Only a commit writes it; typing never does.
  const [committedText, setCommittedText] = useState<string | null>(null);

  // #826 — `Autocomplete` now owns arrow/Enter navigation and highlight
  // tracking (the engine no longer hand-rolls a `role=combobox`/`listbox`
  // pair). `handleHomeEndKeys={false}` on the element below keeps Home/End
  // native (FR-031). Two things `Autocomplete` does NOT know about stay here:
  // FR-032 (Down Arrow at the loaded end with `hasMore` requests the next
  // page) and the Escape cancellation contract (US2: restore committed/blank
  // text, retain the discovery window, call the page's `onEscape`).
  const highlightedIdRef = useRef<string | null>(null);

  // US3 (T035): exact-identity resolution. The controlled sync may hand the
  // engine a full typed entity the DISCOVERY window never contained (a row-
  // owned or late-sorting identity). The engine commits it directly AND, when
  // an exact read is available, validates it through that read — a scoped 404
  // or transport failure enters `unavailable` (never a first-result
  // substitution). The read is generation-owned like every other request: a
  // superseded transition's resolution is dropped.
  const fetchExactRef = useRef(fetchExact);
  fetchExactRef.current = fetchExact;

  const containerRef = useRef<HTMLDivElement>(null);
  const pendingEligibilityKeyRef = useRef<FlockEligibilityKey | null>(null);

  const { retry, loadMore, onQueryChange, runReplacement } = usePickerDiscovery({ stateRef, setState, debounceRef, highlightedIdRef, discoveryGenRef, pendingEligibilityKeyRef, fetchPage, disabled, id, open, eligibilityKey });
  const { commit, retryUnavailable, cancelExploration, clearSelection } = usePickerSelection({ stateRef, setState, setCommittedText, selectionGenRef, debounceRef, highlightedIdRef, fetchExactRef, id, onCommit, onClear });

  // Open (or first discovery): fetch the unfiltered first page. The eligibility
  // key is read from stateRef (not a closure) so that a closed-then-reopened
  // picker with a changed eligibility key issues the request under the CURRENT
  // key, not a stale one. When the key changed while closed, the eligibility
  // effect (declared in usePickerDiscovery.ts) has already synchronously set the phase to
  // "closed" (resetting the window), so this effect's "closed" check passes and
  // the fetch uses the updated key from state.
  //
  // When eligibility changes and open=true in the SAME commit, the eligibility
  // effect runs first (declaration order) and sets pendingEligibilityKeyRef.
  // This effect reads that ref and skips — the debounced replacement (scheduled
  // by the eligibility effect) handles the request under the new key.
  useEffect(() => {
    if (!open) {
      // `<Autocomplete>` remounts on every open with a clean highlight, but
      // this ref outlives it. Left stale, a reopen onto a retained window
      // (FR-018) whose end had been reached would treat the first ArrowDown
      // as `atEnd` and load a page instead of moving the highlight.
      highlightedIdRef.current = null;
      if (debounceRef.current !== null) {
        window.clearTimeout(debounceRef.current);
        debounceRef.current = null;
      }
      // US2 fix: if the picker closes before the eligibility debounce fires,
      // clear the pending signal so a future reopen is not suppressed. Also
      // reset the phase to "closed" if it was "debouncing" (close-before-
      // debounce must leave a reopenable state, not a wedge).
      pendingEligibilityKeyRef.current = null;
      if (stateRef.current.discovery.phase === "debouncing" || stateRef.current.discovery.phase === "replacing") {
        discoveryGenRef.current += 1;
        setState((prev) => ({
          ...prev,
          discovery: { ...prev.discovery, phase: "closed", items: [], hasMore: false, cursor: 0 },
        }));
      }
      return;
    }
    // If the eligibility effect just fired (same commit), it scheduled a
    // debounced replacement under the new key. Skip to avoid a duplicate
    // immediate request under the old key.
    if (pendingEligibilityKeyRef.current !== null) return;
    const state = stateRef.current;
    if (state.discovery.phase !== "closed") return;
    const gen = ++discoveryGenRef.current;
    void runReplacement(state.discovery.normalizedQuery, gen);
  }, [open, runReplacement]);

  // #735 — an open from the trigger hands the keyboard to the search input
  // and selects the committed name, so the first keystroke starts a search
  // rather than appending to "Sim House A". Declared AFTER the open effect so
  // it never runs before the input exists in the same commit. The Retry paths
  // in the discovery and selection hooks still call focus() themselves: those run from a button that has
  // already taken focus, so this effect (keyed on `open`) does not fire.
  //
  // Select-all stays (typing must replace the name, #735), but a selection
  // wider than the field scrolls the browser to its active end, showing the
  // TAIL of a long name. `"backward"` puts the active end at index 0, and
  // `scrollLeft = 0` forces the head into view, which the direction alone
  // did not reliably do in Chromium.
  useEffect(() => {
    if (!open || disabled) return;
    const inputEl = document.getElementById(id);
    if (inputEl instanceof HTMLInputElement) {
      inputEl.focus();
      inputEl.setSelectionRange(0, inputEl.value.length, "backward");
      inputEl.scrollLeft = 0;
    }
  }, [open, disabled, id]);

  // Exploration (FR-020): the visible text differs from the committed label.
  // A committed picker with an untouched field is not exploring.
  const committedName = state.selection.entity?.name ?? null;
  const exploring = committedName === null
    ? state.discovery.rawQuery.trim() !== ""
    : state.discovery.rawQuery !== committedName;
  // US2: canSubmit is true when the picker is safe to write with. For
  // required pickers: a committed entity is present. For optional pickers:
  // no exploration in progress (blank/uninitialized/committed all qualify).
  const canSubmit = !exploring && (state.selection.phase === "committed"
    || ((state.selection.phase === "blank" || state.selection.phase === "uninitialized") && !required && !requestedId));

  // Snapshot emission: only when committed/selectionPhase/exploring/canSubmit
  // actually change. A parent that calls setSnapshot with the received object
  // must not loop merely because the callback identity is fresh on every
  // render — the engine holds the latest callback in a ref and compares the
  // four snapshot fields against the last emitted values.
  const onSnapshotRef = useRef(onSnapshot);
  onSnapshotRef.current = onSnapshot;
  const lastSnapshotRef = useRef<{ committed: T | null; selectionPhase: SelectionPhase; exploring: boolean; canSubmit: boolean } | null>(null);
  useEffect(() => {
    const last = lastSnapshotRef.current;
    if (last && last.committed === state.selection.entity
      && last.selectionPhase === state.selection.phase
      && last.exploring === exploring
      && last.canSubmit === canSubmit) return;
    lastSnapshotRef.current = { committed: state.selection.entity, selectionPhase: state.selection.phase, exploring, canSubmit };
    onSnapshotRef.current({ committed: state.selection.entity, selectionPhase: state.selection.phase, exploring, canSubmit });
  });

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
    // by the `requestedId` effect below (T038), which fires only when the page
    // explicitly names an ID it does not have the full entity for.
    // The controlledCommitted path admits the entity as-is (US1/US2 behavior).
    // Row-owned / late-sorting identity resolution (exact GET) is handled by
    // the `requestedId` effect below (T038), which fires only when the page
    // explicitly names an ID it does not have the full entity for.
  }, [controlledCommitted, controlledGeneration]);

  // US3 (T038): a page-named row-owned ID the engine does not yet have the
  // full entity for (not in the discovery window, not the current committed
  // selection). Resolved through the exact read; a failure enters
  // `unavailable` (never a first-result substitution). Generation-owned.
  const previousRequestedIdRef = useRef(requestedId ?? null);
  useEffect(() => {
    const reqId = requestedId ?? null;
    const previousRequestedId = previousRequestedIdRef.current;
    previousRequestedIdRef.current = reqId;
    if (!reqId) {
      if (previousRequestedId === null) return;
      const gen = ++selectionGenRef.current;
      setCommittedText(null);
      setState((prev) => ({
        ...prev,
        selection: { entity: null, requestedId: null, phase: "blank", transitionGeneration: gen },
        discovery: { ...prev.discovery, rawQuery: "" },
      }));
      return;
    }
    if (!fetchExactRef.current) return;
    // Already committed to this exact id, or it is in the window: admit as-is.
    const inWindow = stateRef.current.discovery.items.some((x) => x.id === reqId);
    const alreadyCommitted = stateRef.current.selection.entity?.id === reqId;
    if (inWindow || alreadyCommitted) return;
    // Debounce by the controlled generation so a rapid open/close doesn't
    // double-fire; the read is owned by the SELECTION-transition generation
    // (it never gates discovery requests).
    const gen = ++selectionGenRef.current;
    setCommittedText(null);
    setState((prev) => ({
      ...prev,
      selection: { entity: null, requestedId: reqId, phase: "resolving", transitionGeneration: gen },
    }));
    void fetchExactRef.current(reqId).then((resolved) => {
      if (selectionGenRef.current !== gen) return;
      // Sync the discovery window to the resolved identity (same contract as
      // retryUnavailable): without it, the committed picker still reports
      // exploring=true and withholds a safe write.
      setCommittedText(resolved.name);
      setState((prev) => ({
        ...prev,
        selection: { entity: resolved, requestedId: null, phase: "committed", transitionGeneration: gen },
        discovery: { ...prev.discovery, rawQuery: resolved.name },
      }));
    }).catch(() => {
      if (selectionGenRef.current !== gen) return;
      setCommittedText(null);
      setState((prev) => ({
        ...prev,
        selection: { entity: null, requestedId: reqId, phase: "unavailable", transitionGeneration: gen },
      }));
    });
  }, [requestedId, controlledGeneration]);

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
  }, [open]);

  // US2: Escape handler — the page provides the close callback. The engine
  // holds it in a ref so onKey's closure stays stable.
  const onEscapeRef = useRef<(() => void) | null>(null);
  onEscapeRef.current = onEscape ?? null;

  // FR-032: Down Arrow at the loaded end with `hasMore` requests the next
  // page instead of moving the highlight (there is nowhere further to move
  // it). Wired onto `Autocomplete`'s own `onKeyDown` prop, which
  // `useAutocomplete.js`'s `getRootProps` calls BEFORE its own switch, so
  // `highlightedIdRef` still holds the option the PREVIOUS keypress left
  // highlighted — the same "already at the end" check the hand-rolled
  // `onArrow` used to make against `activeId`.
  //
  // Escape is ALSO handled here rather than through `Autocomplete`'s own
  // `onClose` callback: `useAutocomplete.js`'s own Escape case calls
  // `event.stopPropagation()` after closing the popup, which — because
  // `FlockPicker`/`CustomerPicker` render inside dialogs whose own
  // Escape-to-close listens on an ancestor — swallowed a SINGLE Escape press
  // that used to close both the exploration AND the dialog at once
  // (SalesPage's new-order picker test, #826 round 1). Setting
  // `defaultMuiPrevented` tells `getRootProps`'s `handleKeyDown` (which calls
  // our handler FIRST, then checks that flag before its own switch) to skip
  // its Escape case entirely, so only OUR `preventDefault` runs and the event
  // keeps bubbling — restoring the pre-#826 behavior exactly.
  const handleRootKeyDown = useCallback((e: KeyboardEvent<HTMLDivElement> & { defaultMuiPrevented?: boolean }) => {
    if (disabled) return;
    if (e.key === "Escape") {
      e.preventDefault();
      e.defaultMuiPrevented = true;
      cancelExploration();
      onEscapeRef.current?.();
      return;
    }
    if (e.key !== "ArrowDown") return;
    const d = stateRef.current.discovery;
    if (d.items.length === 0) return;
    const atEnd = highlightedIdRef.current === d.items[d.items.length - 1].id;
    if (atEnd && d.hasMore && d.phase === "ready") {
      // Also suppress `Autocomplete`'s own ArrowDown handling for THIS
      // press: `options` has not grown yet (the extension is async), so its
      // `changeHighlightedIndex` would move against the STALE, still-short
      // list — and because `disableListWrap` defaults to `false`, moving
      // past the last valid index WRAPS to the first option instead of
      // clamping. Left alone, every boundary press would silently snap the
      // highlight back to the top of a growing list instead of holding
      // still for the extension to land — found via
      // `named-entity-picker.spec.ts`'s real-browser keyboard-paging test,
      // not reasoned about. The next ArrowDown (after `options` grows) again
      // gets no special handling and moves forward normally.
      e.preventDefault();
      e.defaultMuiPrevented = true;
      void loadMore();
    }
  }, [disabled, loadMore, cancelExploration]);

  const d = state.discovery;
  // The clone (see `value` below) is memoised by id/name: `useAutocomplete`'s
  // `syncHighlightedIndex` closes over `value` by reference and re-runs when
  // it changes, so a fresh clone every render re-synced the highlight to the
  // committed row on unrelated renders, including one mid page-load.
  const selectedEntity = state.selection.entity;
  const clonedSelectedValue = useMemo(
    () => (selectedEntity ? { ...selectedEntity } : null),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [selectedEntity?.id, selectedEntity?.name],
  );
  const showLoading = d.phase === "replacing" || d.phase === "extending" || d.phase === "debouncing";
  // The input value IS the discovery's raw text — no committed-text fallback.
  // After a commit the field shows the committed name (rawQuery was set to it);
  // typing explores and the committed entity survives in `selection`.
  const displayText = d.rawQuery;
  // The retention label (FR-018): the committed name, shown as a sibling of
  // the input while the visible text differs from it. Never inside the listbox.
  const showCommitted = committedText !== null && d.rawQuery !== committedText;

  // US3 (T034): the stable aria-live region's announcement text — one localized
  // string per state, mirroring what a screen reader should hear. Visually
  // hidden (CSS); the transient inline spans remain for sighted users.
  const liveMessage =
    d.phase === "replacing" || d.phase === "extending" || d.phase === "debouncing"
      ? t("loading")
      : d.phase === "replacement-error"
        ? t("searchFailed")
      : d.phase === "empty"
        ? t("noResults")
        : state.selection.phase === "unavailable"
          ? t("unavailable")
          : d.phase === "ready" || d.phase === "extension-error"
            ? t("results", { count: d.items.length })
            : "";

  // #826 — the status/alert/Load-more/Clear footer renders INSIDE
  // `Autocomplete`'s floating Paper, as a sibling of the `<ul role="listbox">`
  // it supplies as `children` — never through `slotProps.listbox`, which
  // would put a `<button>` where ARIA only allows `option`/`group`.
  const footerData = {
    t, showLoading, d, disabled, required,
    committedEntity: state.selection.entity,
    unavailable: state.selection.phase === "unavailable",
    retry, loadMore, retryUnavailable, clearSelection, liveMessage,
    pinnedChoice,
  };
  const pickerPaper = usePickerPaper(footerData);

  // US2: closed state renders exactly ONE trigger in the normal form slot.
  // The combobox/listbox are absent. Open state: no trigger, just the
  // searchable combobox in the same position.
  if (!open) {
    return (
      <div ref={containerRef} className={`named-picker${disabled ? " disabled" : ""}`}>
        <PickerClosedState id={id} label={label} trigger={trigger} disabled={disabled} required={required} state={state} t={t} retryUnavailable={retryUnavailable} />
      </div>
    );
  }

  return (
    <div ref={containerRef} className={`named-picker${disabled ? " disabled" : ""}`}>
      <PickerOpenState
        t={t}
        d={d}
        disabled={disabled}
        required={required}
        id={id}
        label={label}
        showLoading={showLoading}
        displayText={displayText}
        showCommitted={showCommitted}
        committedText={committedText}
        clonedSelectedValue={clonedSelectedValue}
        commit={commit}
        onQueryChange={onQueryChange}
        handleRootKeyDown={handleRootKeyDown}
        highlightedIdRef={highlightedIdRef}
        pickerPaper={pickerPaper}
      />
    </div>
  );
}
