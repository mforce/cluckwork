import { useTranslation } from "react-i18next";
import { Box, TextField } from "@mui/material";
import { FilterDateField } from "../components/FilterBar";
import {
  auditActionLabel,
  ENTITY_TYPE_VALUES,
  entityTypeLabel,
  type EntityTypeValue,
} from "../i18n/enums";

// The audit filter bar and its clear button, moved out of AuditPage (#1215).
// It renders exactly where it did before. All filter state and every write
// stays on the page — the callbacks each build a full copy of the current
// query string (INV-2) — so this component only reads values and forwards
// events.
export function AuditFilters({
  entityTypeFilter,
  actionFilter,
  availableActions,
  fromFilter,
  toFilter,
  isNarrowed,
  onEntityTypeChange,
  onActionChange,
  onDateChange,
  onClear,
}: {
  entityTypeFilter: EntityTypeValue | "";
  actionFilter: string;
  availableActions: readonly string[];
  fromFilter: string;
  toFilter: string;
  isNarrowed: boolean;
  onEntityTypeChange: (type: string) => void;
  onActionChange: (action: string) => void;
  onDateChange: (field: "from" | "to", value: string) => void;
  onClear: () => void;
}) {
  const { t } = useTranslation("audit");
  const { t: tc } = useTranslation("common");
  return (
    <>
      <Box className="audit-filters" data-testid="audit-filters">
          <TextField
            select
            label={t("entityTypeFilterLabel")}
            value={entityTypeFilter}
            size="small"
            slotProps={{ select: { native: true }, inputLabel: { shrink: true } }}
            onChange={(e) => onEntityTypeChange(e.target.value)}
          >
            <option value="">{t("allEntityTypesOption")}</option>
            {ENTITY_TYPE_VALUES.map((et) => (
              <option key={et} value={et}>{entityTypeLabel(et)}</option>
            ))}
          </TextField>
          <TextField
            select
            label={t("actionFilterLabel")}
            value={actionFilter}
            size="small"
            slotProps={{ select: { native: true }, inputLabel: { shrink: true } }}
            onChange={(e) => onActionChange(e.target.value)}
          >
            <option value="">{t("allActionsOption")}</option>
            {availableActions.map((a) => (
              <option key={a} value={a}>{auditActionLabel(a)}</option>
            ))}
          </TextField>
          <FilterDateField label={t("fromLabel")} value={fromFilter}
            onChange={(e) => onDateChange("from", e.target.value)} />
          <FilterDateField label={t("toLabel")} value={toFilter}
            onChange={(e) => onDateChange("to", e.target.value)} />
      </Box>
      {isNarrowed && (
        <button className="link audit-clear-filters" type="button" onClick={onClear}>
          {tc("clearFiltersButton")}
        </button>
      )}
    </>
  );
}
