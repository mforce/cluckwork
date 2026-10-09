import { TextField } from "@mui/material";
import type { TextFieldProps } from "@mui/material";

// #308/#798 — the current-password field every step-up uses. The password lives
// only in the caller's state, which clears it as soon as /auth/step-up settles.
export function StepUpPasswordField({
  value, onChange, ...field
}: Omit<TextFieldProps, "type" | "value" | "onChange" | "slotProps"> & {
  value: string;
  onChange: (value: string) => void;
}) {
  return (
    <TextField
      {...field}
      type="password"
      value={value}
      slotProps={{ htmlInput: { required: true, maxLength: 256, autoComplete: "current-password" } }}
      onChange={(e) => onChange(e.target.value)}
    />
  );
}
