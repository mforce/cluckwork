// #1215 file split — the OPEN-state presentation: the committed-label sibling
// and the `Autocomplete` combobox. Moved verbatim out of NamedEntityPicker.tsx;
// the engine still renders the container div, so the div is reconciled (not
// remounted) across open/close toggles exactly as before. This component owns
// no hooks, tickets or refs — every value arrives as a prop.

import Autocomplete from "@mui/material/Autocomplete";
import TextField from "@mui/material/TextField";
import Box from "@mui/material/Box";
import type { AutocompleteRenderInputParams } from "@mui/material/Autocomplete";
import type { PaperProps } from "@mui/material/Paper";
import type React from "react";
import type { KeyboardEvent, MutableRefObject } from "react";
import type { TFunction } from "i18next";
import type { DiscoveryState } from "./types";
import type { NamedEntity } from "./engineTypes";

interface PickerOpenStateProps<T extends NamedEntity> {
  t: TFunction<"namedEntityPicker">;
  /** The engine's discovery window (rows, cursor, phase). */
  d: DiscoveryState<T>;
  disabled: boolean;
  required: boolean;
  id: string;
  label: string;
  showLoading: boolean;
  displayText: string;
  showCommitted: boolean;
  committedText: string | null;
  clonedSelectedValue: T | null;
  commit: (entity: T) => void;
  onQueryChange: (raw: string) => void;
  handleRootKeyDown: (e: KeyboardEvent<HTMLDivElement> & { defaultMuiPrevented?: boolean }) => void;
  highlightedIdRef: MutableRefObject<string | null>;
  pickerPaper: (props: PaperProps) => React.ReactElement;
}

export function PickerOpenState<T extends NamedEntity>({ t, d, disabled, required, id, label, showLoading, displayText, showCommitted, committedText, clonedSelectedValue, commit, onQueryChange, handleRootKeyDown, highlightedIdRef, pickerPaper }: PickerOpenStateProps<T>) {
  return (
    <>
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
        // long-standing `mousedown` listener (in NamedEntityPicker.tsx): `Autocomplete` also
        // treats an outside click as a "blur" close, and wiring both would
        // double-fire `onOutsideClick`.
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
        // `document.body`): the engine's own outside-click listener (in NamedEntityPicker.tsx)
        // tests `containerRef.current.contains(event.target)`, which a
        // portalled popper would defeat — a click on an option would read as
        // "outside" and wrongly cancel exploration.
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
    </>
  );
}
