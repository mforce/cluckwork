import type { Dispatch, SetStateAction } from "react";
import { useTranslation } from "react-i18next";
import { Divider, TextField, Typography } from "@mui/material";
import type { TextFieldProps } from "@mui/material";
import type { StepUpProof } from "../api/client";
import { useAuth } from "../auth/useAuth";
import { useMe } from "../session/SessionContext";
import { SecondFactorCodeField } from "./SecondFactorCodeField";

// #1398 — the lab's proof() in every step-up dialog: "Confirm it's you", what
// to enter, and who is signed in, above the fields. The hint reads the
// session's two_factor claim, the same fact the code field reads, so the
// words and the fields always agree.
export function ConfirmItsYou({ value, onChange, disabled = false }: {
  value: StepUpProof;
  onChange: Dispatch<SetStateAction<StepUpProof>>;
  disabled?: boolean;
}) {
  const { t } = useTranslation("users");
  const { t: ta } = useTranslation("account");
  const { twoFactor } = useAuth();
  const me = useMe();
  return (
    <>
      <Divider />
      <Typography component="h3" variant="h3">{t("confirmItsYouHeading")}</Typography>
      <p className="muted">{ta(twoFactor ? "proofHint" : "proofHintPassword")}</p>
      {me && (
        <Typography variant="caption" sx={{ overflowWrap: "anywhere" }}>
          {t("signedInAsCaption", { email: me.email })}
        </Typography>
      )}
      <StepUpProofFields
        label={t("stepUpFieldLabel")}
        codeLabel={t("stepUpCodeFieldLabel")}
        value={value}
        onChange={onChange}
        disabled={disabled}
      />
    </>
  );
}

// #308/#798 — the current-password field every step-up uses, and for a user
// with two-factor authentication on, the code field too (#1279). The proof
// lives only in the caller's state, which clears it as soon as /auth/step-up
// settles.
export function StepUpProofFields({
  value, onChange, disabled, codeLabel, ...field
}: Omit<TextFieldProps, "type" | "value" | "onChange" | "slotProps"> & {
  value: StepUpProof;
  onChange: Dispatch<SetStateAction<StepUpProof>>;
  // A shorter code label, for a dialog too narrow for the full one at 390px.
  codeLabel?: string;
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
          {...(codeLabel !== undefined && { label: codeLabel })}
          value={value.code ?? ""}
          disabled={disabled}
          onChange={(code) => onChange((proof) => ({ ...proof, code }))}
        />
      )}
    </>
  );
}
