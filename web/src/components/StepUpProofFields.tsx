import type { Dispatch, SetStateAction } from "react";
import { TextField } from "@mui/material";
import type { TextFieldProps } from "@mui/material";
import type { StepUpProof } from "../api/client";
import { useAuth } from "../auth/useAuth";
import { SecondFactorCodeField } from "./SecondFactorCodeField";

// #308/#798 — the current-password field every step-up uses, and for a user
// with two-factor authentication on, the code field too (#1279). The proof
// lives only in the caller's state, which clears it as soon as /auth/step-up
// settles.
export function StepUpProofFields({
  value, onChange, disabled, ...field
}: Omit<TextFieldProps, "type" | "value" | "onChange" | "slotProps"> & {
  value: StepUpProof;
  onChange: Dispatch<SetStateAction<StepUpProof>>;
}) {
  const { twoFactor } = useAuth();
  // The code field marks itself required the way the caller's password label
  // does: the Users dialogs write a literal asterisk, /connect writes none.
  const marked = typeof field.label === "string" && field.label.trimEnd().endsWith("*");
  return (
    <>
      <TextField
        {...field}
        disabled={disabled}
        type="password"
        value={value.password}
        slotProps={{ htmlInput: { required: true, maxLength: 256, autoComplete: "current-password" } }}
        onChange={(e) => {
          const password = e.target.value;
          onChange((proof) => ({ ...proof, password }));
        }}
      />
      {twoFactor && (
        <SecondFactorCodeField
          required={marked}
          value={value.code ?? ""}
          disabled={disabled}
          onChange={(code) => onChange((proof) => ({ ...proof, code }))}
        />
      )}
    </>
  );
}
