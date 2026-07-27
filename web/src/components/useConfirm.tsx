import { useCallback, useEffect, useId, useRef, useState } from "react";
import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
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

type Pending = AskBase & { kind: "confirm" | "reason" };

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
  const resolveRef = useRef<((value: boolean | string | null) => void) | null>(null);
  const dismissValueRef = useRef<boolean | null>(false);
  const reasonRef = useRef<HTMLTextAreaElement>(null);

  const settle = useCallback((value: boolean | string | null) => {
    const resolve = resolveRef.current;
    resolveRef.current = null;
    setPending(null);
    setReason("");
    setReasonError(null);
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
      resolve: (value: boolean | string | null) => void,
    ) => {
      resolveRef.current?.(dismissValueRef.current);
      resolveRef.current = resolve;
      dismissValueRef.current = dismissValue;
      setReason("");
      setReasonError(null);
      setPending(next);
    },
    [],
  );

  const confirm = useCallback(
    (req: AskBase) =>
      new Promise<boolean>((resolve) => {
        // Sound by construction: `kind` decides what settle() is ever called
        // with, and a "confirm" only ever settles with a boolean.
        open({ ...req, kind: "confirm" }, false, resolve as (v: boolean | string | null) => void);
      }),
    [open],
  );

  const askReason = useCallback(
    (req: AskBase) =>
      new Promise<string | null>((resolve) => {
        open({ ...req, kind: "reason" }, null, resolve as (v: boolean | string | null) => void);
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
  const reasonId = `${ids}-reason`;
  const errorId = `${ids}-error`;
  const bodyId = `${ids}-body`;

  // Focus lands by DOM order, which puts it in the right place for free:
  // Cancel for a yes/no (a stray Enter must not deplete a flock), the textarea
  // for a reason (there is nothing to decide until they have typed).
  const confirmDialog = (
    <Dialog
      open={pending !== null}
      title={pending?.title ?? ""}
      onClose={dismiss}
      describedBy={pending ? bodyId : undefined}
    >
      {pending && (
        <>
          <div className="confirm-body" id={bodyId}>{pending.body}</div>
          {pending.kind === "reason" && (
            <label htmlFor={reasonId}>
              {t("reasonLabel")}
              <textarea
                id={reasonId}
                ref={reasonRef}
                rows={3}
                required
                value={reason}
                aria-invalid={reasonError !== null}
                aria-describedby={reasonError ? errorId : undefined}
                onChange={(e) => {
                  setReason(e.target.value);
                  if (reasonError) setReasonError(null);
                }}
              />
            </label>
          )}
          {reasonError && <p className="error" id={errorId}>{reasonError}</p>}
          <div className="dialog-foot">
            <button type="button" className="link" onClick={dismiss}>{tc("cancel")}</button>
            <button
              type="button"
              className={pending.destructive ? "btn-danger" : undefined}
              onClick={() => accept(pending)}
            >
              {pending.confirmLabel}
            </button>
          </div>
        </>
      )}
    </Dialog>
  );

  return { confirm, askReason, confirmDialog };
}
