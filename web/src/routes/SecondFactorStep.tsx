import { useState } from "react";
import type { FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { Alert, Button, Stack, TextField, Typography } from "@mui/material";
import { useAuth } from "../auth/useAuth";
import { ApiError } from "../api/client";
import { FARM_SUSPENDED } from "../api/session";
import { BusyButton } from "../components/BusyButton";
import { usePendingAction } from "../components/usePendingAction";
import i18n from "../i18n";
import { messageFor } from "./signInMessage";

// #1274 "What a caller sees": the code step's answers. Expired and suspended
// send the user back to the password step; every other refusal keeps them here.
const SECOND_FACTOR_INCORRECT = "Auth.SecondFactorIncorrect";
const SECOND_FACTOR_KEY_UNAVAILABLE = "Auth.SecondFactorKeyUnavailable";
const SECOND_FACTOR_THROTTLED = "Auth.SecondFactorThrottled";

function codeStepMessage(err: unknown, typed: string): { message: string; restart: boolean } | null {
  if (!(err instanceof ApiError)) return null;
  if (err.title === FARM_SUSPENDED) return { message: i18n.t("auth:farmSuspended"), restart: true };
  if (err.status === 401) return { message: i18n.t("auth:secondFactorExpired"), restart: true };
  // The clock hint helps only an authenticator code (maintainer, #1278).
  if (err.title === SECOND_FACTOR_INCORRECT)
    return {
      message: /^\d{6}$/.test(typed.replace(/ /g, ""))
        ? `${i18n.t("auth:secondFactorIncorrect")} ${i18n.t("auth:secondFactorClockHint")}`
        : i18n.t("auth:secondFactorIncorrect"),
      restart: false,
    };
  if (err.title === SECOND_FACTOR_KEY_UNAVAILABLE) return { message: i18n.t("auth:secondFactorKeyUnavailable"), restart: false };
  if (err.title === SECOND_FACTOR_THROTTLED) return { message: i18n.t("auth:secondFactorThrottled"), restart: false };
  return null;
}

interface Props {
  farmCode: string;
  // #1274 — held in the parent's state only, never in storage or a URL.
  challenge: string;
  // Back to the password step, with the message to show there, if any.
  onRestart: (message: string | null) => void;
}

// A completed sign-in flips isAuthenticated, and the Login page's effect then
// takes the user to where they were going.
export function SecondFactorStep({ farmCode, challenge, onRestart }: Props) {
  const { t } = useTranslation("auth");
  const { completeSecondFactor } = useAuth();
  const { busy, run } = usePendingAction();
  const [code, setCode] = useState("");
  const [error, setError] = useState<string | null>(null);

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    await run("secondFactor", async () => {
      setError(null);
      try {
        await completeSecondFactor(farmCode, challenge, code);
      } catch (err) {
        const answer = codeStepMessage(err, code);
        if (answer?.restart) onRestart(answer.message);
        else setError(answer?.message ?? messageFor(err));
      }
    });
  }

  return (
    <Stack component="form" spacing={2} onSubmit={onSubmit}>
      <Typography variant="h2">{t("secondFactorTitle")}</Typography>
      <Typography variant="body2" color="text.secondary">{t("secondFactorIntro")}</Typography>
      <TextField
        label={t("secondFactorCode")}
        id="one-time-code"
        name="code"
        value={code}
        onChange={(e) => setCode(e.target.value)}
        autoComplete="one-time-code"
        required
        autoFocus
        slotProps={{
          htmlInput: { autoCapitalize: "characters", autoCorrect: "off", spellCheck: false, maxLength: 32 },
        }}
      />
      {error && <Alert severity="error">{error}</Alert>}
      <BusyButton variant="contained" type="submit" busy={busy}>
        {busy ? t("secondFactorVerifying") : t("secondFactorSubmit")}
      </BusyButton>
      <Button variant="text" type="button" disabled={busy} onClick={() => onRestart(null)}>
        {t("secondFactorBack")}
      </Button>
    </Stack>
  );
}
