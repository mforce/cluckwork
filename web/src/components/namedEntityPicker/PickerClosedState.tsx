// #1215 file split — the CLOSED-state presentation: the read-only trigger
// field plus the adjacent unavailable/Retry affordances. Moved verbatim out of
// NamedEntityPicker.tsx; the engine still renders the container div and the
// open-state combobox.

import React from "react";
import TextField from "@mui/material/TextField";
import InputAdornment from "@mui/material/InputAdornment";
import type { ReactNode } from "react";
import type { TFunction } from "i18next";
import { ChevronDown } from "lucide-react";
import type { EngineState, NamedEntity } from "./engineTypes";

interface PickerClosedStateProps<T extends NamedEntity> {
  /** Stable control id for the input (the label's htmlFor). */
  id: string;
  /** Visible, translated label text (page-owned translation). */
  label: string;
  /** The page's trigger control (its native selector). */
  trigger: ReactNode;
  disabled: boolean;
  required: boolean;
  /** The engine's state; only the selection phase is read (unavailable). */
  state: EngineState<T>;
  t: TFunction<"namedEntityPicker">;
  retryUnavailable: () => Promise<void>;
}

export function PickerClosedState<T extends NamedEntity>({ id, label, trigger, disabled, required, state, t, retryUnavailable }: PickerClosedStateProps<T>) {
  // Closed state (owner redesign, 2026-09-17): the committed value reads
  // as the SAME outlined MUI field as the open search — label in the
  // border, an end adornment signaling "this reopens a search" — not a
  // plain button (a #896-review finding: the old trigger read as "a plain
  // dark box with no affordance that it reopens a search"). Only the
  // CHROME moves from a hand-rolled `<button>`/`<span>` to a read-only
  // `TextField`: click, Enter and Space all open the search exactly as the
  // trigger's own `onClick` did before.
  //
  // The `trigger` prop still supplies BOTH the interaction contract (its
  // `onClick` opens the picker; its own `disabled` combines with the
  // engine's) and the DISPLAYED text. The text stays trigger-owned rather
  // than switching to the engine's own `committedText`: each caller
  // already computes "committed name or placeholder" itself, and that
  // placeholder differs screen to screen ("No flocks yet", "All", a
  // loading/unavailable fallback on Sales' filter) — `committedText` alone
  // has no placeholder to fall back to.
  const hasTrigger = React.isValidElement(trigger);
  const triggerProps = hasTrigger
    ? (trigger as React.ReactElement<{
        onClick?: (e: React.SyntheticEvent) => void;
        disabled?: boolean;
        children?: ReactNode;
      }>).props
    : undefined;
  const triggerDisabled = disabled || !!triggerProps?.disabled;
  const activate = (e: React.SyntheticEvent) => {
    if (triggerDisabled) return;
    triggerProps?.onClick?.(e);
  };
  const displayValue = typeof triggerProps?.children === "string" ? triggerProps.children : "";
  return (
    <>
      {hasTrigger ? (
        <TextField
          id={id}
          label={label}
          required={required}
          disabled={triggerDisabled}
          value={displayValue}
          onClick={activate}
          // MUI's default ("medium") horizontal padding, on a field this
          // narrow inside a two-up phone row (`.form-grid .named-picker`'s
          // 8rem floor), left almost no room for the value text — a real
          // capture at 390px showed a committed "Capture Test Flock"
          // rendered as just "C…". `size="small"` reclaims that padding.
          size="small"
          // #916 review — `size="small"`'s own wrapper height (~37px) sits
          // under the 44px tap-target floor every other control on this
          // screen meets. Verified against every pre-existing render site
          // (nine `FlockPicker` + two `CustomerPicker`, across seven
          // screens): none sets a conflicting height, so this only ever
          // grows the field.
          sx={{ "& .MuiInputBase-root": { minHeight: 44 } }}
          slotProps={{
            htmlInput: {
              readOnly: true,
              "aria-haspopup": "listbox",
              "aria-expanded": false,
              "aria-required": required || undefined,
              onKeyDown: (e: React.KeyboardEvent<HTMLInputElement>) => {
                if (e.key !== "Enter" && e.key !== " ") return;
                e.preventDefault();
                activate(e);
              },
            },
            input: {
              endAdornment: (
                <InputAdornment position="end">
                  {/* #864's own Daily Entry mockup (daily-entry-390.png)
                      draws this affordance as a chevron, not a magnifier —
                      checked against it before picking between the two
                      icons the owner's instruction named as acceptable. */}
                  <ChevronDown size={18} strokeWidth={1.5} aria-hidden focusable={false} />
                </InputAdornment>
              ),
            },
          }}
        />
      ) : (
        // No trigger element was provided (optional on the typed adapters):
        // there is no control to activate and no value to show as a field,
        // so render only the visible label text.
        <span className="named-picker-label">{label}</span>
      )}
      {/* US3 remediation — the engine used to render unavailable/Retry ONLY
          in the open branch above, so a page whose picker never opens
          (History/Feed row-owned ids resolved without a combobox) or is
          `disabled` (Water's edit-locked capture picker) had NO recovery
          affordance when the exact GET failed: the trigger just showed its
          fallback text with no way back in. Adjacent to the trigger, same
          translated strings, same GET-only Retry — deliberately NOT gated
          on `disabled` (see retryUnavailable's own comment). */}
      {state.selection.phase === "unavailable" && (
        <span className="named-picker-status" role="alert">{t("unavailable")} — {t("unavailableExplanation")}</span>
      )}
      {state.selection.phase === "unavailable" && (
        <button type="button" className="named-picker-retry link" onClick={() => void retryUnavailable()}>
          {t("retry")}
        </button>
      )}
    </>
  );
}
