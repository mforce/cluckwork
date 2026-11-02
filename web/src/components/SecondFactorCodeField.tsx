import { TextField } from "@mui/material";
import type { TextFieldProps } from "@mui/material";
import { useTranslation } from "react-i18next";

// #1274/#1279 — the one field for an authenticator or recovery code, at the
// login code step, step-up and change-password. Like the password, the code
// lives only in the caller's state.
export function SecondFactorCodeField({
  value, onChange, ...field
}: Omit<TextFieldProps, "type" | "value" | "onChange" | "slotProps"> & {
  value: string;
  onChange: (value: string) => void;
}) {
  const { t } = useTranslation("auth");
  return (
    <TextField
      label={t("secondFactorCode")}
      name="code"
      autoComplete="one-time-code"
      required
      {...field}
      value={value}
      onChange={(e) => onChange(e.target.value)}
      slotProps={{
        htmlInput: { autoCapitalize: "characters", autoCorrect: "off", spellCheck: false, maxLength: 32 },
      }}
    />
  );
}
