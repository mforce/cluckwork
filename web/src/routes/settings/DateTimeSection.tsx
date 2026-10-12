import type { Dispatch, SetStateAction } from "react";
import { useTranslation } from "react-i18next";
import { Stack, TextField } from "@mui/material";
import { weekdayLabel } from "../../i18n/enums";

// Mirrors the server's validators (UpdateFarmSettingsValidator + Account) so a
// too-long value is refused by the field rather than by a 400.
const MAX_FORMAT = 32;

const WEEKDAYS = [
  "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday",
];

// #452 — common date/time format presets, offered as a dropdown so most
// people never need to know .NET custom-format-string syntax (dd vs MM vs
// yyyy, which one is the day and which is the month). "Custom…" reveals the
// pre-existing free-text field for anything not listed — the server
// validates whatever ends up there either way
// (UpdateFarmSettingsValidator.BeAUsableFormat), this dropdown only changes
// how most people arrive at a value, not what's accepted.
export const DATE_FORMAT_PRESETS = ["MM/dd/yyyy", "dd/MM/yyyy", "yyyy-MM-dd"];
export const TIME_FORMAT_PRESETS = ["h:mm tt", "HH:mm"];
const CUSTOM_FORMAT_OPTION = "__custom__";

// A stored value that isn't blank and isn't one of the offered presets means
// the farm is already using a custom format — the dropdown must open on
// "Custom…" (with the real value in the revealed field) rather than
// silently falling back to "Follow the locale" and discarding it on save.
export const isCustomFormat = (value: string, presets: string[]) =>
  value !== "" && !presets.includes(value);

export function DateTimeSection({
  firstDayOfWeek, setFirstDayOfWeek, dateFormat, setDateFormat, dateFormatCustom,
  setDateFormatCustom, timeFormat, setTimeFormat, timeFormatCustom, setTimeFormatCustom,
}: {
  firstDayOfWeek: string;
  setFirstDayOfWeek: Dispatch<SetStateAction<string>>;
  dateFormat: string;
  setDateFormat: Dispatch<SetStateAction<string>>;
  dateFormatCustom: boolean;
  setDateFormatCustom: Dispatch<SetStateAction<boolean>>;
  timeFormat: string;
  setTimeFormat: Dispatch<SetStateAction<string>>;
  timeFormatCustom: boolean;
  setTimeFormatCustom: Dispatch<SetStateAction<boolean>>;
}) {
  const { t } = useTranslation("settings");

  return (
      <Stack spacing={2} sx={{ maxWidth: "40rem" }}>

        <TextField select label={t("firstDayOfWeekLabel")} value={firstDayOfWeek}
          onChange={(e) => setFirstDayOfWeek(e.target.value)}
          slotProps={{ select: { native: true }, inputLabel: { shrink: true } }}>
          <option value="">{t("followLocaleOption")}</option>
          {WEEKDAYS.map((d) => <option key={d} value={d}>{weekdayLabel(d)}</option>)}
        </TextField>

        <TextField select label={t("dateFormatLabel")}
          value={dateFormatCustom ? CUSTOM_FORMAT_OPTION : dateFormat}
          onChange={(e) => {
            if (e.target.value === CUSTOM_FORMAT_OPTION) { setDateFormatCustom(true); return; }
            setDateFormatCustom(false);
            setDateFormat(e.target.value);
          }}
          slotProps={{ select: { native: true }, inputLabel: { shrink: true } }}
        >
          <option value="">{t("followLocaleOption")}</option>
          {DATE_FORMAT_PRESETS.map((p) => <option key={p} value={p}>{p}</option>)}
          <option value={CUSTOM_FORMAT_OPTION}>{t("customFormatOption")}</option>
        </TextField>
        {dateFormatCustom && (
          <TextField label={t("customDateFormatLabel")} value={dateFormat}
            onChange={(e) => setDateFormat(e.target.value)}
            slotProps={{ htmlInput: { maxLength: MAX_FORMAT } }} />
        )}

        <TextField select label={t("timeFormatLabel")}
          value={timeFormatCustom ? CUSTOM_FORMAT_OPTION : timeFormat}
          onChange={(e) => {
            if (e.target.value === CUSTOM_FORMAT_OPTION) { setTimeFormatCustom(true); return; }
            setTimeFormatCustom(false);
            setTimeFormat(e.target.value);
          }}
          slotProps={{ select: { native: true }, inputLabel: { shrink: true } }}
        >
          <option value="">{t("followLocaleOption")}</option>
          {TIME_FORMAT_PRESETS.map((p) => <option key={p} value={p}>{p}</option>)}
          <option value={CUSTOM_FORMAT_OPTION}>{t("customFormatOption")}</option>
        </TextField>
        {timeFormatCustom && (
          <TextField label={t("customTimeFormatLabel")} value={timeFormat}
            onChange={(e) => setTimeFormat(e.target.value)}
            slotProps={{ htmlInput: { maxLength: MAX_FORMAT } }} />
        )}
      </Stack>
  );
}
