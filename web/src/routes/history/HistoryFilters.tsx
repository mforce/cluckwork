import { Box, Button } from "@mui/material";
import { useTranslation } from "react-i18next";
import type { Flock } from "../../api/cluckwork";
import { FilterBar, FilterDateField, FILTER_PICKER_SX } from "../../components/FilterBar";
import { FlockPicker } from "../../components/FlockPicker";

export function HistoryFilters({
  flockFilter, setFlockFilter, flockFilterEntity, setFlockFilterEntity, filterPickerOpen,
  setFilterPickerOpen, from, setFrom, to, setTo, flocks,
}: {
  flockFilter: string;
  setFlockFilter: (flockId: string) => void;
  flockFilterEntity: Flock | null;
  setFlockFilterEntity: (flock: Flock | null) => void;
  filterPickerOpen: boolean;
  setFilterPickerOpen: (open: boolean) => void;
  from: string;
  setFrom: (from: string) => void;
  to: string;
  setTo: (to: string) => void;
  flocks: Flock[];
}) {
  const { t } = useTranslation("history");
  const { t: tc } = useTranslation("common");

  const flockName = (id: string) => {
    // #512 (T038) — the filter's committed identity is a genuine user pick
    // (onCommit), always a full entity; there is no external/deep-link
    // source that could name an id the picker's exact GET might fail to
    // resolve (Opus review). Used ONLY for the filter trigger — never for a
    // row (see rowFlockName on the page).
    if (id === flockFilter && flockFilterEntity)
      return flockFilterEntity.name;
    return flocks.find((f) => f.id === id)?.name ?? id.slice(0, 8);
  };

  return (
    <FilterBar>
      <Box sx={FILTER_PICKER_SX}>
        {/* #512 (T038) — the read-only filter became an optional
            eligibility=all FlockPicker: the filter keeps its exact id
            ownership (the list fetches by `flockFilter`), and a row-owned
            identity outside the capped discovery window resolves through
            the exact GET with an explicit unavailable state. */}
        <FlockPicker
          label={t("flockLabel")}
          eligibility="all"
          required={false}
          open={filterPickerOpen}
          requestedId={flockFilter || null}
          onSnapshot={(snap) => {
            if (snap.committed) setFlockFilterEntity(snap.committed);
          }}
          onCommit={(f) => {
            setFlockFilter(f.id);
            setFlockFilterEntity(f);
            setFilterPickerOpen(false);
          }}
          onClear={() => {
            setFlockFilter("");
            setFlockFilterEntity(null);
          }}
          onEscape={() => setFilterPickerOpen(false)}
          onOutsideClick={() => setFilterPickerOpen(false)}
          trigger={
            <button type="button" className="named-picker-trigger"
              onClick={() => setFilterPickerOpen(true)}>
              {flockFilter === "" ? t("allFlocksOption") : flockName(flockFilter)}
            </button>
          }
        />
      </Box>
      <FilterDateField label={t("fromLabel")} value={from} onChange={(e) => setFrom(e.target.value)} />
      <FilterDateField label={t("toLabel")} value={to} onChange={(e) => setTo(e.target.value)} />
      <Button variant="outlined" color="inherit" sx={{ borderRadius: "4px" }} onClick={() => { setFlockFilter(""); setFlockFilterEntity(null); setFilterPickerOpen(false); setFrom(""); setTo(""); }}>{tc("clearFiltersButton")}</Button>
    </FilterBar>
  );
}
