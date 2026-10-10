import { Box, Button, Checkbox, FormControlLabel, TextField } from "@mui/material";
import { useTranslation } from "react-i18next";
import { CustomerPicker } from "../../components/CustomerPicker";
import { FilterBar } from "../../components/FilterBar";
import i18n from "../../i18n";
import { statusLabel } from "../../i18n/enums";
import { PICKER_SX } from "./salesUi";
import type { OrderFilterState } from "./useOrderFilters";

// #987 — one filter set, rendered inline on desktop and inside the phone
// filter dialog the chip row opens.
export function OrderFilters({ filter, canSettle, isDesktop }: {
  filter: OrderFilterState;
  canSettle: boolean;
  isDesktop: boolean;
}) {
  const { t } = useTranslation("sales");
  const { t: tc } = useTranslation("common");
  const {
    statusFilter, setStatusFilter, searchParams, setSearchParams, customerFilter, unpaidFilter,
    setCustomerFilterEntity, customerFilterSnapshot, setCustomerFilterSnapshot,
    customerFilterPickerOpen, setCustomerFilterPickerOpen, clearFilters, customerFilterName,
  } = filter;
  return (
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
}
