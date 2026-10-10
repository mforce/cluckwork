import { useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { useSearchParams } from "react-router";
import type { Customer } from "../../api/cluckwork";
import type { PickerSnapshot } from "../../components/NamedEntityPicker";
import i18n from "../../i18n";
import { normalizeCanonicalGuid } from "./orderMath";

// list filters (#24: status/customer/paged)
export function useOrderFilters(canSettle: boolean) {
  const { t } = useTranslation("sales");
  const [statusFilter, setStatusFilter] = useState("");
  // #512 US5 (T057) — `customerId` in the URL is the SOLE source of truth for
  // the customer filter (FR-046): direct navigation, reload, edits, and
  // browser Back/Forward all flow through `searchParams` (react-router keeps
  // it live across all four). Derived fresh every render — no local copy to
  // drift from the URL. Malformed values normalize to "" (FR-048): treated as
  // absent, never sent to the server, URL left untouched.
  const [searchParams, setSearchParams] = useSearchParams();
  const customerFilter = normalizeCanonicalGuid(searchParams.get("customerId"));
  // #769 — `unpaid=1` in the URL, beside `customerId`. It answers a question
  // worth bookmarking and sharing ("what does this customer still owe me"), and
  // the customer filter already lives there, so one link carries both. A
  // boolean needs NONE of the identity-resolution machinery below
  // (customerFilterStale, the synchronous hide, the picker's unavailable
  // phase): that exists because a customer id must be resolved to an entity,
  // and there is nothing here to resolve.
  //
  // KNOWN LIMITATION, stated rather than hidden: `statusFilter` is still local
  // state, so a shared link restores the customer and unpaid filters and not
  // the status one. Moving it is a separate slice.
  //
  // Outside the money tier the URL value is treated as absent and never sent,
  // so the server's 403 on `unpaid=true` is unreachable from this screen.
  const unpaidFilter = searchParams.get("unpaid") === "1" && canSettle;
  // #655 fix increment 2 — the empty-state variant and its "Clear filters"
  // reach must track every filter sent to the API (line ~289: status AND
  // customerId), not customerFilter alone, or a status-only filter that
  // matches nothing shows the truly-empty copy on a farm that has orders.
  const hasActiveFilter = Boolean(customerFilter) || Boolean(statusFilter) || unpaidFilter;
  // #512 US5 (T057, FR-049) — the filter's committed identity: the row-owned
  // entity the picker's exact GET resolved for a well-formed `customerId` (or
  // a genuine user pick). A failed exact read enters the picker's own
  // `unavailable` phase — Retry re-issues ONLY the GET, Clear removes
  // `customerId` from the URL. Never a raw id, never a first-result
  // substitution, never rewritten to All.
  const [customerFilterEntity, setCustomerFilterEntity] = useState<Customer | null>(null);
  const [customerFilterSnapshot, setCustomerFilterSnapshot] = useState<PickerSnapshot<Customer>>({
    committed: null, selectionPhase: "uninitialized", exploring: false, canSubmit: true,
  });
  const [customerFilterPickerOpen, setCustomerFilterPickerOpen] = useState(false);
  // #512 US5 (T057, FR-050) — a URL identity change must hide the previous
  // identity's rows and the filter's own displayed name SYNCHRONOUSLY, before
  // any effect/debounce/request — not one paint later, when `usePagedList`'s
  // own reload effect would otherwise be the first thing to notice. Adjusting
  // state during render (the React-documented pattern for "reset state when a
  // prop changes") does both: `customerFilterEntity` clears so the trigger
  // never flashes the OLD name, and `customerFilterStale` gates the table
  // for exactly the render(s) between the URL changing and the new load's own
  // `reloading` taking over (cleared once that load lands, in SalesPage's
  // effect — never before, so a synchronously-hidden window isn't reshown by
  // this render alone before the replacement is ready).
  const lastCustomerFilterRef = useRef(customerFilter);
  const [customerFilterStale, setCustomerFilterStale] = useState(false);
  if (customerFilter !== lastCustomerFilterRef.current) {
    lastCustomerFilterRef.current = customerFilter;
    if (customerFilterEntity !== null) setCustomerFilterEntity(null);
    if (!customerFilterStale) setCustomerFilterStale(true);
  }

  const clearFilters = () => {
    const next = new URLSearchParams(searchParams);
    next.delete("customerId");
    next.delete("unpaid");
    setCustomerFilterEntity(null);
    setStatusFilter("");
    setSearchParams(next);
  };

  // The committed filter identity's own name, or null for "no customer filter".
  // The chip and the picker trigger disagree only about how to say "all".
  const customerFilterName = customerFilter === ""
    ? null
    : customerFilterEntity?.name
      ?? (customerFilterSnapshot.selectionPhase === "unavailable"
        ? t("filterCustomerUnavailable")
        : i18n.t("namedEntityPicker:loading"));

  return {
    statusFilter, setStatusFilter, searchParams, setSearchParams,
    customerFilter, unpaidFilter, hasActiveFilter, setCustomerFilterEntity,
    customerFilterSnapshot, setCustomerFilterSnapshot, customerFilterPickerOpen, setCustomerFilterPickerOpen,
    customerFilterStale, setCustomerFilterStale, clearFilters, customerFilterName,
  };
}

export type OrderFilterState = ReturnType<typeof useOrderFilters>;
