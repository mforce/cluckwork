import { useState } from "react";
import type { FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { Alert, Button, Stack, Typography } from "@mui/material";
import { useAuth } from "../auth/useAuth";
import { ApiError } from "../api/client";
import { FARM_SUSPENDED } from "../api/session";
import { BusyButton } from "../components/BusyButton";
import { SecondFactorCodeField } from "../components/SecondFactorCodeField";
import { usePendingAction } from "../components/usePendingAction";
import i18n from "../i18n";
import { secondFactorMessage } from "../lib/secondFactorMessage";
import { messageFor } from "./signInMessage";

// #1274 "What a caller sees": expired and suspended send the user back to the
// password step; every other refusal keeps them here.
function codeStepMessage(err: unknown): { message: string; restart: boolean } | null {
  if (!(err instanceof ApiError)) return null;
  if (err.title === FARM_SUSPENDED) return { message: i18n.t("auth:farmSuspended"), restart: true };
  if (err.status === 401) return { message: i18n.t("auth:secondFactorExpired"), restart: true };
  const message = secondFactorMessage(err);
  return message === null ? null : { message, restart: false };
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
        const answer = codeStepMessage(err);
        if (answer?.restart) onRestart(answer.message);
        else setError(answer?.message ?? messageFor(err));
      }
    });
  }

  return (
    <Stack component="form" spacing={2} onSubmit={onSubmit}>
      <Typography variant="h2">{t("secondFactorTitle")}</Typography>
      <Typography variant="body2" color="text.secondary">{t("secondFactorIntro")}</Typography>
      <SecondFactorCodeField id="one-time-code" value={code} onChange={setCode} autoFocus />
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
