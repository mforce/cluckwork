// #1215 file split — the popup footer: the stable `slots.paper` renderer that
// hosts the pinned choice, the listbox, the aria-live region and the status /
// retry / Load more / Clear controls. Moved verbatim out of
// NamedEntityPicker.tsx, lazy-init included: `PickerPaper` must keep a STABLE
// component identity for the life of the picker instance, so the hook creates
// it exactly once.

import Paper from "@mui/material/Paper";
import type { PaperProps } from "@mui/material/Paper";
import { useRef } from "react";
import type { ReactNode } from "react";
import type React from "react";
import type { TFunction } from "i18next";
import type { DiscoveryState } from "./types";
import type { NamedEntity } from "./engineTypes";

/** Everything the footer renderer reads; rebuilt each render, read via ref. */
interface PickerFooterData<T extends NamedEntity> {
  t: TFunction<"namedEntityPicker">;
  showLoading: boolean;
  d: DiscoveryState<T>;
  disabled: boolean;
  required: boolean;
  committedEntity: T | null;
  unavailable: boolean;
  retry: () => Promise<void>;
  loadMore: () => Promise<void>;
  retryUnavailable: () => Promise<void>;
  clearSelection: () => void;
  liveMessage: string;
  pinnedChoice?: ReactNode;
}

/**
 * Returns the `slots.paper` component: created once per picker instance, then
 * reading the latest footer values through `footerRef` on every render, so
 * `Autocomplete` never remounts the popup subtree on a keystroke.
 */
export function usePickerPaper<T extends NamedEntity>(footerData: PickerFooterData<T>): (props: PaperProps) => React.ReactElement {
  // `PickerPaper` itself must keep a STABLE identity across renders:
  // `Autocomplete` mounts it via `as={PaperSlot}`, so a fresh function value
  // every render would remount the whole popup subtree on every keystroke —
  // including the stable `aria-live` region below, whose contract (US3 T034)
  // is that it is the SAME node across loading/results/error transitions.
  // `footerRef` carries the latest values the footer reads; the `useRef`
  // lazy-init below creates the component exactly once per picker instance.
  const footerRef = useRef<PickerFooterData<T>>(footerData);
  footerRef.current = footerData;
  const pickerPaperRef = useRef<((props: PaperProps) => React.ReactElement) | null>(null);
  let pickerPaper = pickerPaperRef.current;
  if (pickerPaper === null) {
    pickerPaper = function PickerPaper({ children, ...paperProps }: PaperProps) {
      const f = footerRef.current;
      return (
        <Paper {...paperProps}>
          {f.pinnedChoice}
          {children}
          <div className="named-picker-meta">
            {/* US3 (T034): a STABLE mounted aria-live region — the SAME node
                across loading/results/error transitions — so assistive tech
                announces the picker's state without the visible spans being
                re-created (and without moving focus off the input). It is
                deliberately NOT role="status": the transient loading span
                below owns that role for the visible UI, and a second
                [role=status] would make getByRole("status") ambiguous. Its
                text mirrors the state for screen readers. */}
            <span className="named-picker-live" aria-live="polite" aria-atomic="true" aria-busy={f.showLoading || undefined}>
              {f.liveMessage}
            </span>
            {f.showLoading && <span className="named-picker-status" role="status">{f.t("loading")}</span>}
            {f.d.phase === "empty" && <span className="named-picker-status" role="status">{f.t("noResults")}</span>}
            {f.d.phase === "replacement-error" && (
              <span className="named-picker-status" role="alert">{f.t("searchFailed")}</span>
            )}
            {f.d.phase === "replacement-error" && (
              <button type="button" className="named-picker-retry link" onClick={() => void f.retry()} disabled={f.disabled}>
                {f.t("retry")}
              </button>
            )}
            {f.d.phase === "extension-error" && (
              <span className="named-picker-status" role="alert">{f.t("loadMoreFailed")}</span>
            )}
            {f.d.phase === "extension-error" && (
              <button type="button" className="named-picker-retry link" onClick={() => void f.retry()} disabled={f.disabled}>
                {f.t("retry")}
              </button>
            )}
            {f.d.hasMore && (
              <button type="button" className="named-picker-loadmore link" onClick={() => void f.loadMore()} disabled={f.showLoading || f.disabled}>
                {f.t("loadMore")}
              </button>
            )}
            {/* US3 (T034/T038): an unavailable exact identity (scoped 404 /
                transport failure on the exact GET) renders the translated
                unavailable label and a keyboard-reachable Retry — never a raw
                ID or a first-result substitution. The Retry re-runs the exact
                read; focus returns to the input on success. */}
            {f.unavailable && (
              <span className="named-picker-status" role="alert">{f.t("unavailable")} — {f.t("unavailableExplanation")}</span>
            )}
            {/* Deliberately NOT gated on `disabled` (US3 remediation): Retry
                re-resolves a FIXED identity via the exact GET, not ordinary
                discovery/selection, which stays disabled above. */}
            {f.unavailable && (
              <button type="button" className="named-picker-retry link" onClick={() => void f.retryUnavailable()}>
                {f.t("retry")}
              </button>
            )}
            {!f.required && f.committedEntity && !f.disabled && (
              <button type="button" className="named-picker-clear link" onClick={f.clearSelection}>
                {f.t("clear")}
              </button>
            )}
          </div>
        </Paper>
      );
    };
    pickerPaperRef.current = pickerPaper;
  }
  return pickerPaper;
}
