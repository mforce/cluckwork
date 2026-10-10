import { useRef, useState } from "react";
import { Box, Button, Checkbox, FormControlLabel, TextField } from "@mui/material";
import { useTranslation } from "react-i18next";
import { useSearchParams } from "react-router";
import type { Customer } from "../../api/cluckwork";
import { CustomerPicker } from "../../components/CustomerPicker";
import { FilterBar } from "../../components/FilterBar";
import type { PickerSnapshot } from "../../components/NamedEntityPicker";
import i18n from "../../i18n";
import { statusLabel } from "../../i18n/enums";
import { normalizeCanonicalGuid } from "./orderMath";
import { PICKER_SX } from "./salesUi";

// list filters (#24: status/customer/paged)
export function useOrderFilters(canSettle: boolean, isDesktop: boolean) {
  const { t } = useTranslation("sales");
  const { t: tc } = useTranslation("common");
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


  // #987 — one filter set, rendered inline on desktop and inside the phone
  // filter dialog the chip row opens.
  const filters = (
      <FilterBar>
        <TextField
          select
          label={t("status")}
          value={statusFilter}
          size="small"
          // Shrink the label so it does not overlap the empty-value placeholder.
          slotProps={{ select: { native: true }, inputLabel: { shrink: true } }}
          onChange={(e) => setStatusFilter(e.target.value)}
        >
          <option value="">{t("allOption")}</option>
          <option value="Draft">{statusLabel("Draft")}</option>
          <option value="Confirmed">{statusLabel("Confirmed")}</option>
          <option value="Cancelled">{statusLabel("Cancelled")}</option>
          <option value="Voided">{statusLabel("Voided")}</option>
        </TextField>
        <Box sx={PICKER_SX}>
          {/* #512 US5 (T057) — `customerId` in the URL is the sole source of
              truth (FR-046); select/clear clone the CURRENT URLSearchParams
              and touch only `customerId` (FR-047), preserving every unrelated
              key (`status` included, once it moves to the URL — none does
              today, but the clone-and-set pattern costs nothing to get right
              now). Malformed values never reach here (`requestedId` is ""),
              so the picker shows blank/All for them, never an unavailable
              state — a malformed id is absent, not inaccessible (FR-048). */}
          <CustomerPicker
            label={t("customer")}
            required={false}
            open={customerFilterPickerOpen}
            requestedId={customerFilter || null}
            onSnapshot={(snap) => {
              setCustomerFilterSnapshot(snap);
              if (snap.committed) setCustomerFilterEntity(snap.committed);
            }}
            onCommit={(c) => {
              const next = new URLSearchParams(searchParams);
              next.set("customerId", c.id);
              setCustomerFilterEntity(c);
              setSearchParams(next);
              setCustomerFilterPickerOpen(false);
            }}
            onClear={() => {
              const next = new URLSearchParams(searchParams);
              next.delete("customerId");
              setCustomerFilterEntity(null);
              setSearchParams(next);
            }}
            onEscape={() => setCustomerFilterPickerOpen(false)}
            onOutsideClick={() => setCustomerFilterPickerOpen(false)}
            trigger={
              <button type="button" className="named-picker-trigger"
                onClick={() => setCustomerFilterPickerOpen(true)}>
                {customerFilterName ?? t("allOption")}
              </button>
            }
          />
          {/* FR-049 — an unavailable filter identity must offer Clear even
              while the picker is closed (the generic engine's own Clear only
              renders inside the open combobox, and only once something is
              actually committed — never during `unavailable`, where nothing
              is). This is the page-owned affordance that closes the gap. */}
          {customerFilterSnapshot.selectionPhase === "unavailable" && (
            <button type="button" className="link" onClick={() => {
              const next = new URLSearchParams(searchParams);
              next.delete("customerId");
              setCustomerFilterEntity(null);
              setSearchParams(next);
            }}>
              {i18n.t("namedEntityPicker:clear")}
            </button>
          )}
        </Box>
        {/* #769 — the money tier only. Same clone-and-set discipline as the
            customer filter above: touch `unpaid` and nothing else. */}
        {canSettle && (
          <FormControlLabel
            label={t("unpaidOnlyFilter")}
            slotProps={{ typography: { color: "text.secondary" } }}
            control={
              <Checkbox checked={unpaidFilter}
                onChange={(e) => {
                  const next = new URLSearchParams(searchParams);
                  if (e.target.checked) next.set("unpaid", "1");
                  else next.delete("unpaid");
                  setSearchParams(next);
                }} />
            }
          />
        )}
        {/* #987 — on a phone this control moves into the filter dialog's
            footer, beside Done; the chip row has no width to spare. */}
        {isDesktop && <Button variant="outlined" color="inherit" sx={{ borderRadius: "4px" }} onClick={clearFilters}>{tc("clearFiltersButton")}</Button>}
      </FilterBar>
  );

  return {
    statusFilter, customerFilter, unpaidFilter, hasActiveFilter,
    customerFilterStale, setCustomerFilterStale, customerFilterName, clearFilters, filters,
  };
}
