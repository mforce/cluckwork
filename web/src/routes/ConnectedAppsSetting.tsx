import { useId } from "react";
import { useTranslation } from "react-i18next";
import { ChevronDown } from "lucide-react";
import { Accordion, AccordionDetails, AccordionSummary, Checkbox, FormControlLabel, Typography } from "@mui/material";

// #1146 — a Farm settings field, saved with the rest, so a Checkbox rather than a Switch.
export function ConnectedAppsSetting({ checked, onChange }: { checked: boolean; onChange: (checked: boolean) => void }) {
  const { t } = useTranslation("settings");
  const { t: tApps } = useTranslation("connectedApps");
  const hintId = useId();
  return (
    <Accordion disableGutters slotProps={{ transition: { unmountOnExit: true } }}>
      <AccordionSummary expandIcon={<ChevronDown size={18} aria-hidden />}>
        <Typography variant="h3" component="span">{tApps("heading")}</Typography>
      </AccordionSummary>
      <AccordionDetails>
        <FormControlLabel label={t("allowConnectedAppsLabel")}
          control={<Checkbox checked={checked} onChange={(e) => onChange(e.target.checked)}
            slotProps={{ input: { "aria-describedby": hintId } }} />} />
        <Typography id={hintId} variant="body2" color="text.secondary">{t("allowConnectedAppsHint")}</Typography>
      </AccordionDetails>
    </Accordion>
  );
}
