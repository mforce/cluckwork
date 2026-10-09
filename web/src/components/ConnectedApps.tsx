import { useState } from "react";
import { useTranslation } from "react-i18next";
import type { AppConnection } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { useFarm } from "../farm/useFarm";
import { daysSince, farmDateOf, relativeTime } from "../lib/relativeTime";
import { FarmDate } from "./FarmDate";
import { useConfirm } from "./useConfirm";

// #799 — an app unused this long gets the idle marker and, on Account, the nudge.
export const IDLE_DAYS = 30;

// The consent screen's words for each permission (#798), so Connected apps names
// what was allowed exactly as the person saw it when they allowed it.
const SCOPE_KEYS: Record<string, "connect:scopeRead" | "connect:scopeWrite"> = {
  "farm:read": "connect:scopeRead",
  "daily-entries:write": "connect:scopeWrite",
};
// The server sorts scopes by name; consent lists reading first, and so does this.
const rank = (scope: string) => {
  const index = Object.keys(SCOPE_KEYS).indexOf(scope);
  return index === -1 ? Infinity : index;
};

export function useConnectionFacts() {
  const { t } = useTranslation(["connectedApps", "connect"]);
  const timeZone = useFarm().farm?.timeZoneId;
  const idleDays = (app: AppConnection) => daysSince(app.lastUsedAtUtc ?? app.connectedAtUtc, timeZone);
  const isIdle = (app: AppConnection) => idleDays(app) >= IDLE_DAYS;
  return {
    name: (app: AppConnection) => app.appName ?? t("connect:unnamedApp"),
    can: (app: AppConnection) => [...app.scopes].sort((a, b) => rank(a) - rank(b))
      .map((scope) => SCOPE_KEYS[scope] ? t(SCOPE_KEYS[scope]) : scope).join(", "),
    connected: (app: AppConnection) => <FarmDate iso={farmDateOf(app.connectedAtUtc, timeZone)} />,
    lastUsed: (app: AppConnection) =>
      app.lastUsedAtUtc ? relativeTime(app.lastUsedAtUtc, timeZone) : t("connectedApps:notUsedYet"),
    idleDays,
    isIdle,
    status: (app: AppConnection) => isIdle(app)
      ? <span className="badge badge-warn">{t("connectedApps:idle", { days: idleDays(app) })}</span>
      : <span className="badge badge-ok">{t(app.lastUsedAtUtc ? "connectedApps:inUse" : "connectedApps:notUsedYet")}</span>,
  };
}

// Confirm, disconnect, then say what happens next. A 404 means the app was already
// disconnected (another tab, or the Owner), which leaves the person where they wanted.
export function useDisconnect(reload: () => void) {
  const { t } = useTranslation("connectedApps");
  const { confirm, confirmDialog } = useConfirm();
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function disconnect(app: string, body: string, request: () => Promise<unknown>) {
    setMessage(null);
    setError(null);
    if (!(await confirm({ title: t("confirmTitle", { app }), body, confirmLabel: t("disconnect"), destructive: true }))) return;
    setBusy(true);
    try {
      await request();
      setMessage(t("disconnected", { app }));
    } catch (err) {
      if (err instanceof ApiError && err.status === 404) setMessage(t("disconnected", { app }));
      else setError(t("failed", { app }));
    } finally {
      setBusy(false);
      reload();
    }
  }

  return { disconnect, busy, message, error, confirmDialog };
}
