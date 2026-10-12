// #512 — the shared named-entity picker: transport/selection/snapshot
// contracts plus the one async discovery+commit engine they describe.
//
// THIS FILE IS THE ENGINE'S COMPOSITION ROOT. Pages never import the engine
// directly: they consume the typed `FlockPicker` / `CustomerPicker` adapters
// (FlockPicker.tsx / CustomerPicker.tsx), and the engine itself is
// feature-local. Nothing outside this feature should import from this module,
// and it must never grow a generic catalog/entity-link API — the spec
// (FR-053) forbids a shared framework beyond the two typed adapters.
//
// The engine's internals live in ./namedEntityPicker/: the public contract
// types (types.ts), the internal state shape and props (engineTypes.ts), the
// discovery pipeline (usePickerDiscovery.ts), the selection lifecycle
// (usePickerSelection.ts), the popup footer (PickerPaper.tsx) and the
// closed-state trigger (PickerClosedState.tsx). This file composes them and
// keeps the module's public surface unchanged.
//
// The shapes below mirror `specs/001-searchable-entity-picker/data-model.md`
// (Picker Transport Models, State Transitions) and `contracts/picker-ui.md`
// (Shared Snapshot). US1 lands discovery, paging, commit and retention;
// the US2/US3 selection-transition machinery (exact/default/lifecycle
// generations, unavailable states, Escape/clear, Retry) arrives in T026–T034
// on top of the same state.

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import type { KeyboardEvent } from "react";
import { useTranslation } from "react-i18next";
import Autocomplete from "@mui/material/Autocomplete";
import TextField from "@mui/material/TextField";
import Box from "@mui/material/Box";
import type { AutocompleteRenderInputParams } from "@mui/material/Autocomplete";
import { FLOCK_ELIGIBILITIES } from "./namedEntityPicker/types";
import type { FlockEligibilityKey, NamedEntityPage, DiscoveryPhase, DiscoveryState, SelectionPhase, PickerSelection, PickerSnapshot } from "./namedEntityPicker/types";
import { initialState } from "./namedEntityPicker/engineTypes";
import type { EngineProps, EngineState, NamedEntity } from "./namedEntityPicker/engineTypes";
import { usePickerDiscovery } from "./namedEntityPicker/usePickerDiscovery";
import { usePickerSelection } from "./namedEntityPicker/usePickerSelection";
import { usePickerPaper } from "./namedEntityPicker/PickerPaper";
import { PickerClosedState } from "./namedEntityPicker/PickerClosedState";

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

  // The picker's container: the open popup is a DOM descendant of it (no
  // portal), so the outside-click listener can test `contains` (see
  // usePickerSelection.ts).
  const containerRef = useRef<HTMLDivElement>(null);

  const { retry, loadMore, onQueryChange } = usePickerDiscovery({ stateRef, setState, debounceRef, highlightedIdRef, fetchPage, disabled, id, open, eligibilityKey });
  const { commit, retryUnavailable, cancelExploration, clearSelection } = usePickerSelection({ stateRef, setState, setCommittedText, selectionGenRef, debounceRef, highlightedIdRef, fetchExactRef, containerRef, id, open, onCommit, onClear, onOutsideClick, controlledCommitted, controlledGeneration });

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
      {/* Owner redesign, 2026-09-17: the floating label lives on the
          `TextField` (`renderInput` below) now, not a separate `<label>` —
          `Autocomplete`/`TextField` wire `id`/`InputLabel` themselves, so
          `getByLabelText(label)` still resolves to the real input. */}
      {showCommitted && (
        <div className="named-picker-committed">{committedText}</div>
      )}
      {/* Explicit generics: the committed value is genuinely `T | null`
          (blank/uninitialized are real states). MUI's own `disableClearable`
          PROP ties `DisableClearable` to `true` at the type level too
          (narrowing `value`/`onChange` to exclude `null`), which is why the
          clear icon is suppressed via `slots.clearIndicator` below instead —
          that leaves this generic, and the value type, alone. */}
      <Autocomplete<T, false, false, false>
        id={id}
        open
        // Set here, not on the TextField in `renderInput`: `Autocomplete`
        // forwards `size` into `params.size`, which `{...params}` already
        // spreads onto the TextField below — the same 390px-width overflow
        // fix as the closed-state field's own `size="small"`.
        size="small"
        disabled={disabled}
        options={d.items}
        loading={showLoading}
        inputValue={displayText}
        // A shallow clone, not the entity itself: `useAutocomplete.js`'s
        // `handleValue` bails out (never calls `onChange`) when the newly
        // selected option is `===` the current `value` — reference equality,
        // not `isOptionEqualToValue`. A picker whose committed entity and
        // discovery window happen to hold the SAME object reference (e.g. a
        // page-level default customer, FR-037, fetched once and handed to
        // both `controlledCommitted` and the picker's own discovery request)
        // would then silently no-op on re-clicking that already-committed
        // option — a real regression, caught by SalesPage's new-order dialog
        // test, not merely a test mock artifact: a caching or memoizing data
        // layer could produce the same shared reference in production. The
        // clone guarantees `value !== newValue`, so `onChange` always fires;
        // `isOptionEqualToValue` (by id, below) still drives `aria-selected`.
        // MEMOIZED (not recreated every render) — see `clonedSelectedValue`'s
        // own comment above for why an unmemoized clone here broke Load
        // More's highlight retention.
        value={clonedSelectedValue}
        onChange={(_event, newValue, reason) => {
          // Commit by pointer/Enter (FR-030) — the ONLY two ways MUI's own
          // handling reaches `reason === "selectOption"`. Guarded on
          // `disabled` here (not just on the closed native input) because
          // `Autocomplete`'s own key handling is not itself gated on it.
          if (disabled) return;
          if (reason === "selectOption" && newValue) commit(newValue);
        }}
        onInputChange={(_event, newInputValue, reason) => {
          // Only genuine typing re-discovers. `Autocomplete` also fires this
          // with reason "reset" right after a commit (syncing its internal
          // input value to the selected label) and "clear" from its own
          // clear icon (disabled below) — `commit`/`clearSelection` already
          // own `rawQuery` for those, so re-running discovery for them would
          // wipe the just-loaded results.
          if (reason === "input") onQueryChange(newInputValue);
        }}
        // No `onClose`: Escape is fully handled by `handleRootKeyDown`
        // (`onKeyDown` below), which runs before — and here, instead of —
        // `Autocomplete`'s own Escape case. Outside-click keeps its own
        // long-standing `mousedown` listener (in usePickerSelection.ts):
        // `Autocomplete` also treats an outside click as a "blur" close, and
        // wiring both would double-fire `onOutsideClick`.
        onHighlightChange={(_event, option) => {
          highlightedIdRef.current = option ? option.id : null;
        }}
        onKeyDown={handleRootKeyDown}
        filterOptions={(options) => options}
        getOptionLabel={(option) => option.name}
        getOptionKey={(option) => option.id}
        isOptionEqualToValue={(option, value) => option.id === value.id}
        // One line per option with an ellipsis; the popper's own floor is set
        // below.
        renderOption={({ key, ...optionProps }, option) => (
          <li key={key} {...optionProps}>
            <Box component="span" sx={{ display: "block", overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>
              {option.name}
            </Box>
          </li>
        )}
        // US2/US3 own commit, retry and clear (in the footer below);
        // `Autocomplete`'s built-in clear/popup icons would duplicate that
        // UI, so both are nulled via `slots` (not `disableClearable` — see
        // the generics comment above). `handleHomeEndKeys={false}` keeps
        // Home/End native input behavior (FR-031) — `Autocomplete` jumps to
        // the first/last option on those keys by default. `disableListWrap`
        // stops ArrowDown at the TRUE final option (hasMore already false)
        // from silently jumping back to the first row — found the same way
        // as the `handleRootKeyDown` comment above, via
        // `named-entity-picker.spec.ts`'s keyboard-paging test, which presses
        // ArrowDown at the true end and asserts the highlight stays.
        handleHomeEndKeys={false}
        disableListWrap
        // The popup stays a DOM descendant of `containerRef` (no portal to
        // `document.body`): the engine's own outside-click listener (in
        // usePickerSelection.ts) tests `containerRef.current.contains(event.target)`,
        // which a portalled popper would defeat — a click on an option would
        // read as "outside" and wrongly cancel exploration.
        disablePortal
        clearText={t("clear")}
        closeText={t("close")}
        openText={t("open")}
        loadingText={t("loading")}
        noOptionsText={t("noResults")}
        // `Autocomplete` sizes the popper to the anchor's width, which in a
        // narrow phone row is too little to read a name against; `min-width`
        // beats that inline width, and `bottom-start` keeps it left-aligned
        // with the field.
        slotProps={{
          popper: {
            placement: "bottom-start",
            sx: { minWidth: "min(20rem, calc(100vw - 32px))" },
          },
        }}
        slots={{
          paper: pickerPaper,
          clearIndicator: () => null,
          popupIndicator: () => null,
          // The picker renders its OWN loading/no-results text in the footer
          // (it must also cover "loading while retained rows are still
          // shown", e.g. Load more in flight — a case `Autocomplete`'s own
          // status slot can't reach: it hides once options are non-empty).
          // Suppressing that slot avoids a second, usually-empty
          // `role="status"` node — it otherwise mounts whenever the popup is
          // open, which would make `getByRole("status")` ambiguous.
          status: () => null,
        }}
        renderInput={(params: AutocompleteRenderInputParams) => (
          <TextField
            {...params}
            label={label}
            required={required}
            placeholder={committedText ?? undefined}
            // #916 review — same 44px floor as the closed-state trigger field
            // above; the open search field is the one every mockup measured.
            sx={{ "& .MuiInputBase-root": { minHeight: 44 } }}
            slotProps={{
              ...params.slotProps,
              htmlInput: {
                ...params.slotProps.htmlInput,
                // The literal attribute: `required` above gives the native
                // HTML attribute (and MUI's own asterisk) but not this — no
                // picker label carries its own "*" today (contrast
                // `useConfirm.tsx`'s `reasonLabel`, which does and so skips
                // the `required` prop to avoid a doubled asterisk).
                "aria-required": required || undefined,
              },
            }}
          />
        )}
      />
    </div>
  );
}
