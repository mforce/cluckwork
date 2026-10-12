import type { Dispatch, SetStateAction } from "react";
import { useTranslation } from "react-i18next";
import { Stack, TextField, Typography } from "@mui/material";
import { unitSystemLabel } from "../../i18n/enums";
import type { FarmSettings } from "../../api/cluckwork";

// Mirrors the server's validators (UpdateFarmSettingsValidator + Account) so a
// too-long value is refused by the field rather than by a 400.
const MAX_NAME = 120;
const MAX_LOCALE = 32;
const MAX_TIMEZONE = 64;

const UNIT_SYSTEMS = ["Metric", "Imperial"];

// The browser's own IANA list, so the field offers exactly the zones this
// browser can also FORMAT with — the same table todayIso() reads. The server
// keeps its own (newer or older) list and remains the authority; a zone typed
// by hand is still accepted if it validates there.
//
// Guarded because this runs at MODULE LOAD, and App.tsx imports this screen
// statically: on a browser without Intl.supportedValuesOf the throw would
// happen before React mounts, outside every ErrorBoundary, and white-screen the
// whole app rather than degrade one Setup screen (review of #123). An empty
// list leaves the field a plain text input, which the server validates anyway.
const TIME_ZONES: string[] = (() => {
  try {
    return Intl.supportedValuesOf("timeZone");
  } catch {
    return [];
  }
})();

export function LocalizationSection({
  name, setName, timeZoneId, setTimeZoneId, locale, setLocale, currencyCode,
  setCurrencyCode, unitSystem, setUnitSystem, timeZoneUnknown, timeZoneNoteId,
  currencyNoteId, loaded,
}: {
  name: string;
  setName: Dispatch<SetStateAction<string>>;
  timeZoneId: string;
  setTimeZoneId: Dispatch<SetStateAction<string>>;
  locale: string;
  setLocale: Dispatch<SetStateAction<string>>;
  currencyCode: string;
  setCurrencyCode: Dispatch<SetStateAction<string>>;
  unitSystem: string;
  setUnitSystem: Dispatch<SetStateAction<string>>;
  timeZoneUnknown: boolean;
  timeZoneNoteId: string;
  currencyNoteId: string;
  loaded: FarmSettings;
}) {
  const { t } = useTranslation("settings");

  return (
      <Stack spacing={2} sx={{ maxWidth: "40rem" }}>
        <TextField label={t("farmNameLabel")} value={name} required
          onChange={(e) => setName(e.target.value)}
          slotProps={{ htmlInput: { maxLength: MAX_NAME } }} />

        <TextField label={t("timezoneLabel")} value={timeZoneId} required
          onChange={(e) => setTimeZoneId(e.target.value)}
          slotProps={{
            htmlInput: {
              list: "tz-options", maxLength: MAX_TIMEZONE,
              "aria-describedby": timeZoneUnknown ? timeZoneNoteId : undefined,
            },
          }} />
        <datalist id="tz-options">
          {TIME_ZONES.map((tz) => <option key={tz} value={tz} />)}
        </datalist>
        {/* Describe the field without changing its accessible name. */}
        {timeZoneUnknown && (
          <Typography variant="body2" color="warning.main" sx={{ fontWeight: 600, mt: "-0.5rem" }} id={timeZoneNoteId}>
            {/* The server validates against ITS tzdata, which can be newer than
                this browser's. A zone it accepts but the browser cannot format
                saves fine and then quietly sends every date field back to the
                device's day — the one thing this whole slice removes. */}
            {t("timezoneUnknownWarning")}
          </Typography>
        )}

        <TextField label={t("localeLabel")} value={locale} required placeholder="en-US"
          onChange={(e) => setLocale(e.target.value)}
          slotProps={{ htmlInput: { maxLength: MAX_LOCALE } }} />

        {/* Read-only keeps the locked field and its explanation in the tab order. */}
        <TextField label={t("currencyLabel")} value={currencyCode} required
          onChange={(e) => setCurrencyCode(e.target.value.toUpperCase())}
          slotProps={{
            htmlInput: {
              maxLength: 3, readOnly: !loaded.canChangeCurrency,
              "aria-describedby": loaded.canChangeCurrency ? undefined : currencyNoteId,
            },
          }}
          sx={loaded.canChangeCurrency ? undefined : {
            "& .MuiOutlinedInput-root": { backgroundColor: "action.disabledBackground" },
            "& input": { cursor: "not-allowed" },
          }} />
        {/* §4.6: the rule, at the field it locks, rather than as a 422 after
            the user has typed a new code. */}
        {!loaded.canChangeCurrency && (
          <Typography variant="body2" color="warning.main" sx={{ fontWeight: 600, mt: "-0.5rem" }} id={currencyNoteId}>
            {t("currencyLockedNote", { code: loaded.settings.currencyCode })}
          </Typography>
        )}

        <TextField select label={t("unitSystemLabel")} value={unitSystem}
          onChange={(e) => setUnitSystem(e.target.value)}
          slotProps={{ select: { native: true }, inputLabel: { shrink: true } }}>
          {UNIT_SYSTEMS.map((u) => <option key={u} value={u}>{unitSystemLabel(u)}</option>)}
        </TextField>
      </Stack>
  );
}
