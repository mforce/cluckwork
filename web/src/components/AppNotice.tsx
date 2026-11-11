import { createContext, useCallback, useContext, useMemo, useState } from "react";
import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { Alert } from "@mui/material";
import type { AlertColor } from "@mui/material";
import { useAuth } from "../auth/useAuth";
import { useMissedAnnouncement } from "./useMissedAnnouncement";

// #1398 — the shell's notice strip, above the routed screen so it is the
// first thing read (notices.css). The farm warning and a posted notice both
// use it. A warning or error is an alert said at once, in the farm warning's
// tint; anything else is a status said politely, in the accent tint.
//
// The offscreen region is always mounted, and carries the message only when a
// dialog had the page inert as it arrived (#485). It holds no live ROLE: the
// E2E suite reads a hidden alert or status as "nothing is wrong", so a
// permanent one would answer every such query.
export function ShellNotice({ message, severity, action, onClose }: {
  message: string | null;
  severity: AlertColor;
  action?: ReactNode;
  onClose?: () => void;
}) {
  const { t } = useTranslation("common");
  const missed = useMissedAnnouncement(message);
  const urgent = severity === "warning" || severity === "error";
  return (
    <>
      <p className="sr-only" aria-live={urgent ? "assertive" : "polite"} aria-atomic="true">{missed}</p>
      {message !== null && (
        <Alert severity={severity} className={urgent ? "farm-warning" : "shell-notice"} role={urgent ? "alert" : "status"}
          action={action} onClose={onClose} closeText={t("close")}>
          {message}
        </Alert>
      )}
    </>
  );
}

export interface Notice {
  message: string;
  severity: AlertColor;
}

interface AppNotice {
  notice: Notice | null;
  show: (message: string, severity?: AlertColor) => void;
  dismiss: () => void;
}

// With no provider above, the strip has nothing to show, and a post is a bug.
const NO_HOST: AppNotice = {
  notice: null,
  show: () => { throw new Error("Posting a notice needs an AppNoticeProvider above it."); },
  dismiss: () => undefined,
};
const AppNoticeContext = createContext<AppNotice>(NO_HOST);

export const useAppNotice = (): AppNotice => useContext(AppNoticeContext);

// Above the routes, so a notice outlives a route change and the move from a
// screen outside the shell into it. It belongs to the signed-in user: a
// session that ends, or another user's tokens, clears it.
export function AppNoticeProvider({ children }: { children: ReactNode }) {
  const { isAuthenticated, userId } = useAuth();
  const owner = isAuthenticated ? userId : null;
  // ponytail: one notice at a time, a new one replaces it, and it stays until
  // closed. A queue goes here once two notices can matter at the same time.
  const [notice, setNotice] = useState<Notice | null>(null);
  const [heldFor, setHeldFor] = useState(owner);
  if (heldFor !== owner) {
    setHeldFor(owner);
    setNotice(null);
  }
  const show = useCallback((message: string, severity: AlertColor = "info") => setNotice({ message, severity }), []);
  const dismiss = useCallback(() => setNotice(null), []);
  const value = useMemo(() => ({ notice, show, dismiss }), [notice, show, dismiss]);
  return <AppNoticeContext.Provider value={value}>{children}</AppNoticeContext.Provider>;
}

// The posted notice, in the shell's strip, with its close button.
export function PostedNotice() {
  const { notice, dismiss } = useAppNotice();
  return <ShellNotice message={notice?.message ?? null} severity={notice?.severity ?? "info"} onClose={dismiss} />;
}
