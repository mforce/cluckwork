import { useCallback, useEffect, useId, useRef, useState } from "react";
import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import {
  Button, DialogActions, DialogContentText, FormControl, FormControlLabel,
  FormHelperText, FormLabel, Radio, RadioGroup, TextField,
} from "@mui/material";
import { Dialog } from "./Dialog";

interface AskBase {
  /** Rendered as the heading. Phrase it as the question being asked. */
  title: string;
  /** What actually happens if they say yes. Rendered in a div — blocks are fine. */
  body: ReactNode;
  /** The action button's label. Use the same verb the trigger used. */
  confirmLabel: string;
  /**
   * Paints the action red. For the ones that UNDO or RETIRE something — void,
   * cancel, deplete, archive. Not for one-way steps that simply move a record
   * forward: submitting a day and confirming an order are irreversible too, but
   * they are the ordinary path through the week, and a red button on the most
   * routine action of all would spend the colour where it says nothing.
   */
  destructive?: boolean;
}

/** One option in an `askChoice` picklist. `value` is what the caller gets back. */
export interface Choice {
  value: string;
  label: string;
}

interface AskChoice extends AskBase {
  /** Rendered as radio buttons, in the order given. */
  choices: readonly Choice[];
  /** Labels the picklist. */
  choiceLabel: string;
  /**
   * Values for which the note is mandatory. Everything else takes it or leaves
   * it — an option like "Other" names nothing on its own, so its note carries
   * the whole answer.
   */
  noteRequiredFor: readonly string[];
  /** Labels the note field. */
  noteLabel: string;
  /** Shown under the note when it is required and blank. */
  noteRequiredMessage: string;
  /** Shown under the picklist when nothing is selected. */
  choiceRequiredMessage: string;
}

/** What `askChoice` resolves to when the user goes through with it. */
export interface ChoiceResult {
  value: string;
  note: string | null;
}

type Pending =
  | (AskBase & { kind: "confirm" | "reason" })
  | (AskChoice & { kind: "choice" });

type Settled = boolean | string | ChoiceResult | null;

// F135: the app's own replacement for window.confirm / window.prompt.
//
// Both natives are synchronous, which is the only thing they had going for
// them — a dialog can't return a value inline, so callers `await` instead:
//
//   if (!(await confirm({ ... }))) return;
//
//   const reason = await askReason({ ... });
//   if (reason === null) return;          // never resolves to an empty string
//
// One hook rather than two components, so a screen that needs both shapes
// (Sales needs all four) still renders a single element.
export function useConfirm() {
  const { t } = useTranslation("useConfirm");
  const { t: tc } = useTranslation("common");
  const [pending, setPending] = useState<Pending | null>(null);
  const [reason, setReason] = useState("");
  const [reasonError, setReasonError] = useState<string | null>(null);

  // The awaiting caller's `resolve`, parked outside state: settling it must not
  // depend on a re-render having happened. The value a dismissal resolves to
  // rides alongside, so cancelling never has to re-derive which shape is up.
  const resolveRef = useRef<((value: Settled) => void) | null>(null);
  const dismissValueRef = useRef<boolean | null>(false);
  const reasonRef = useRef<HTMLTextAreaElement>(null);
  const choiceRef = useRef<HTMLInputElement>(null);
  const [choice, setChoice] = useState("");
  const [choiceError, setChoiceError] = useState<string | null>(null);

  const settle = useCallback((value: Settled) => {
    const resolve = resolveRef.current;
    resolveRef.current = null;
    setPending(null);
    setReason("");
    setReasonError(null);
    setChoice("");
    setChoiceError(null);
    resolve?.(value);
  }, []);

  const dismiss = useCallback(() => settle(dismissValueRef.current), [settle]);

  // Asking again while a question is still up would strand the first promise
  // for ever. Answer it as dismissed so its caller unwinds — and settle it
  // BEFORE taking the new resolver, or the incoming promise is the one that
  // gets answered and the outgoing one hangs.
  const open = useCallback(
    (
      next: Pending,
      dismissValue: boolean | null,
      resolve: (value: Settled) => void,
    ) => {
      resolveRef.current?.(dismissValueRef.current);
      resolveRef.current = resolve;
      dismissValueRef.current = dismissValue;
      setReason("");
      setReasonError(null);
      setChoice("");
      setChoiceError(null);
      setPending(next);
    },
    [],
  );

  const confirm = useCallback(
    (req: AskBase) =>
      new Promise<boolean>((resolve) => {
        // Sound by construction: `kind` decides what settle() is ever called
        // with, and a "confirm" only ever settles with a boolean.
        open({ ...req, kind: "confirm" }, false, resolve as (v: Settled) => void);
      }),
    [open],
  );

  const askReason = useCallback(
    (req: AskBase) =>
      new Promise<string | null>((resolve) => {
        open({ ...req, kind: "reason" }, null, resolve as (v: Settled) => void);
      }),
    [open],
  );

  // The third shape: pick one of a closed set, plus a note that some options
  // make mandatory. Same inline-error handling as askReason — the dialog stays
  // open and focus returns to whichever field refused — because a picklist that
  // closed on a missing note would cost the user the option they had chosen.
  const askChoice = useCallback(
    (req: AskChoice) =>
      new Promise<ChoiceResult | null>((resolve) => {
        open({ ...req, kind: "choice" }, null, resolve as (v: Settled) => void);
      }),
    [open],
  );

  // Navigating away with a question on screen must not leave the caller
  // awaiting a promise that can no longer resolve.
  useEffect(() => () => resolveRef.current?.(dismissValueRef.current), []);

  // Takes the question rather than re-reading state: it is only ever rendered
  // inside `pending && (…)`, so a null check here would be unreachable.
  const accept = (current: Pending) => {
    if (current.kind === "confirm") {
      settle(true);
      return;
    }
    if (current.kind === "choice") {
      if (!choice) {
        setChoiceError(current.choiceRequiredMessage);
        choiceRef.current?.focus();
        return;
      }
      const note = reason.trim();
      if (!note && current.noteRequiredFor.includes(choice)) {
        setReasonError(current.noteRequiredMessage);
        reasonRef.current?.focus();
        return;
      }
      settle({ value: choice, note: note || null });
      return;
    }
    const text = reason.trim();
    if (!text) {
      // Inline, and the dialog stays open: window.prompt validated only after
      // it had closed, so a blank reason cost the user everything they typed.
      setReasonError(t("reasonRequired"));
      // Back to the field, not left on the button that just refused. Sighted
      // users get the cursor where the work is; a screen reader announces the
      // error, which it reaches through aria-describedby on focus.
      reasonRef.current?.focus();
      return;
    }
    settle(text);
  };

  const ids = useId();
  const bodyId = `${ids}-body`;
  const choiceErrorId = `${ids}-choice-error`;

  // Focus lands by DOM order, which puts it in the right place for free:
  // Cancel for a yes/no (a stray Enter must not deplete a flock), the field
  // for a reason (there is nothing to decide until they have typed).
  //
  // fullScreenOnPhone={false} — a confirmation is prose and one or two
  // controls, never a form long enough to need a phone's whole screen (D2
  // pair 2, D3.3); it stays a centred Dialog at every width, unlike the form
  // dialogs every route screen opens through this same component.
  const confirmDialog = (
    <Dialog
      open={pending !== null}
      title={pending?.title ?? ""}
      onClose={dismiss}
      describedBy={pending ? bodyId : undefined}
      fullScreenOnPhone={false}
    >
      {pending && (
        <>
          <DialogContentText id={bodyId} sx={{ mb: pending.kind === "confirm" ? 0 : 2 }}>
            {pending.body}
          </DialogContentText>
          {pending.kind === "choice" && (
            <>
              {/* component="fieldset"/"legend" — the same fieldset+legend
                  structure the hand-rolled version used, matching
                  SettingsPage's palette picker. aria-describedby is manual
                  because a fieldset (role="group") carries the error text,
                  which MUI's FormControl does not wire up on its own for an
                  arbitrary child. */}
              <FormControl
                component="fieldset"
                className="choice-set"
                aria-describedby={choiceError ? choiceErrorId : undefined}
                sx={{ mb: 2 }}
              >
                <FormLabel component="legend">{pending.choiceLabel}</FormLabel>
                <RadioGroup
                  name={`${ids}-choice`}
                  value={choice}
                  onChange={(e) => {
                    setChoice(e.target.value);
                    setChoiceError(null);
                    // The note's requirement follows the option, so an error
                    // raised against the previous one no longer describes
                    // anything on screen.
                    setReasonError(null);
                  }}
                >
                  {pending.choices.map((option, index) => (
                    <FormControlLabel
                      key={option.value}
                      value={option.value}
                      label={option.label}
                      control={
                        <Radio
                          slotProps={index === 0 ? { input: { ref: choiceRef } } : undefined}
                        />
                      }
                    />
                  ))}
                </RadioGroup>
                {choiceError && <FormHelperText id={choiceErrorId} error>{choiceError}</FormHelperText>}
              </FormControl>
              <TextField
                inputRef={reasonRef}
                label={pending.noteLabel}
                multiline
                rows={3}
                fullWidth
                required={pending.noteRequiredFor.includes(choice)}
                value={reason}
                error={reasonError !== null}
                helperText={reasonError ?? undefined}
                onChange={(e) => {
                  setReason(e.target.value);
                  if (reasonError) setReasonError(null);
                }}
              />
            </>
          )}
          {pending.kind === "reason" && (
            <TextField
              inputRef={reasonRef}
              label={t("reasonLabel")}
              multiline
              rows={3}
              fullWidth
              required
              value={reason}
              error={reasonError !== null}
              helperText={reasonError ?? undefined}
              onChange={(e) => {
                setReason(e.target.value);
                if (reasonError) setReasonError(null);
              }}
            />
          )}
          <DialogActions sx={{ px: 0, mt: 2 }}>
            <Button onClick={dismiss}>{tc("cancel")}</Button>
            <Button
              variant="contained"
              color={pending.destructive ? "error" : "primary"}
              onClick={() => accept(pending)}
            >
              {pending.confirmLabel}
            </Button>
          </DialogActions>
        </>
      )}
    </Dialog>
  );

  return { confirm, askReason, askChoice, confirmDialog };
}
