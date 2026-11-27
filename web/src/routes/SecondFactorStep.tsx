import { useState } from "react";
import type { FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { Alert, Button, Stack, Typography } from "@mui/material";
import { useAuth } from "../auth/useAuth";
import { ApiError, startSecondFactorPasskey } from "../api/client";
import type { PasskeyAnswer, SecondFactor } from "../api/client";
import { FARM_SUSPENDED } from "../api/session";
import { getPasskey } from "../auth/passkey";
import { BusyButton } from "../components/BusyButton";
import { SecondFactorCodeField } from "../components/SecondFactorCodeField";
import { usePendingAction } from "../components/usePendingAction";
import i18n from "../i18n";
import { secondFactorMessage } from "../lib/secondFactorMessage";
import { messageFor } from "./signInMessage";

// #1274 "What a caller sees": expired and suspended send the user back to the
// password step; every other refusal keeps them here. #1285 adds the
// passkey's refusals (record 1282): 400 Auth.PasskeyRefused, 404 Passkeys.Off.
function codeStepMessage(err: unknown): { message: string; restart: boolean } | null {
  if (!(err instanceof ApiError)) return null;
  if (err.title === FARM_SUSPENDED) return { message: i18n.t("auth:farmSuspended"), restart: true };
  if (err.status === 401) return { message: i18n.t("auth:secondFactorExpired"), restart: true };
  if (err.title === "Auth.PasskeyRefused" || err.title === "Passkeys.Off")
    return { message: i18n.t(`errors:${err.title}`), restart: false };
  const message = secondFactorMessage(err);
  return message === null ? null : { message, restart: false };
}

interface Props {
  farmCode: string;
  // The email typed at the password step, shown above the passkey prompt.
  email: string;
  // #1274 — held in the parent's state only, never in storage or a URL.
  challenge: string;
  // #1285 — what the pending 401's secondFactors offers this user.
  factors: SecondFactor[];
  // The sign-in page learned that passkeys are off here, or unsupported.
  passkeysOff: boolean;
  // Back to the password step, with the message to show there, if any.
  onRestart: (message: string | null) => void;
}

// A completed sign-in flips isAuthenticated, and the Login page's effect then
// takes the user to where they were going.
export function SecondFactorStep({ farmCode, email, challenge, factors, passkeysOff, onRestart }: Props) {
  const { t } = useTranslation("auth");
  const { completeSecondFactor } = useAuth();
  const { busy, run } = usePendingAction();
  const passkeyOffered = factors.includes("passkey") && !passkeysOff;
  const codeOffered = factors.includes("authenticator");
  // #1283 "Sign-in A": the passkey first when it is offered.
  const [usePasskey, setUsePasskey] = useState(passkeyOffered);
  const [code, setCode] = useState("");
  const [error, setError] = useState<string | null>(null);

  async function answer(proof: () => Promise<string | PasskeyAnswer | null>) {
    await run("secondFactor", async () => {
      setError(null);
      try {
        const given = await proof();
        if (given === null) {
          setError(t("passkeySecondCancelled"));
          return;
        }
        await completeSecondFactor(farmCode, challenge, given);
      } catch (err) {
        const message = codeStepMessage(err);
        if (message?.restart) onRestart(message.message);
        else setError(message?.message ?? messageFor(err));
      }
    });
  }

  function onSubmit(e: FormEvent) {
    e.preventDefault();
    void answer(async () => (usePasskey ? getPasskey(await startSecondFactorPasskey(challenge)) : code));
  }

  const switchTo = (passkey: boolean) => {
    setError(null);
    setUsePasskey(passkey);
  };

  return (
    <Stack component="form" spacing={2} onSubmit={onSubmit}>
      <Typography variant="h2">{usePasskey ? t("passkeySecondTitle") : t("secondFactorTitle")}</Typography>
      {usePasskey && (
        // #1283 "Sign-in A": who is signing in, and where. The page holds the
        // farm code, not the farm's name.
        <Stack spacing={0.5}>
          <Typography variant="body2" sx={{ fontWeight: 600, overflowWrap: "anywhere" }}>{email}</Typography>
          <Typography variant="caption" color="text.secondary">
            {t("passkeySecondConfirmed", { farm: farmCode.trim() })}
          </Typography>
        </Stack>
      )}
      <Typography variant="body2" color="text.secondary">
        {usePasskey ? t("passkeySecondIntro") : t("secondFactorIntro")}
      </Typography>
      {!usePasskey && codeOffered && (
        <SecondFactorCodeField id="one-time-code" value={code} onChange={setCode} autoFocus />
      )}
      {/* A passkey-only user on an address where passkeys are off. */}
      {!passkeyOffered && !codeOffered && <Alert severity="warning">{t("passkeySecondOff")}</Alert>}
      {error && <Alert severity="error">{error}</Alert>}
      {(usePasskey || codeOffered) && (
        <BusyButton variant="contained" type="submit" busy={busy}>
          {usePasskey ? t("passkeySecondSubmit") : busy ? t("secondFactorVerifying") : t("secondFactorSubmit")}
        </BusyButton>
      )}
      {usePasskey && codeOffered && (
        <Button variant="outlined" type="button" disabled={busy} onClick={() => switchTo(false)}>
          {t("passkeySecondUseCode")}
        </Button>
      )}
      {!usePasskey && passkeyOffered && (
        <Button variant="text" type="button" disabled={busy} onClick={() => switchTo(true)}>
          {t("passkeySecondSubmit")}
        </Button>
      )}
      {usePasskey && !codeOffered && (
        <Typography variant="body2" color="text.secondary">{t("passkeySecondOnly")}</Typography>
      )}
      <Button variant="text" type="button" disabled={busy} onClick={() => onRestart(null)}>
        {t("secondFactorBack")}
      </Button>
    </Stack>
  );
}
