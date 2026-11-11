import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from "react";
import type { ReactNode, Ref } from "react";
import { useTranslation } from "react-i18next";
import { Alert } from "@mui/material";
import type { AlertColor } from "@mui/material";
import { useAuth } from "../auth/useAuth";
import { MAIN_CONTENT_ID } from "../lib/mainContent";
import { anyDialogOpen, onModalStateChange } from "./Dialog";
import { useMissedAnnouncement } from "./useMissedAnnouncement";

// #1398 — the shell's notice strip, above the routed screen so it is the
// first thing read (notices.css). The farm warning and a posted notice both
// use it. A warning or error is an alert said at once, in the farm warning's
// tint; anything else is a status said politely, in the accent tint.
//
// The offscreen region is always mounted, and carries `spoken` (the message
// by default) only when a dialog had the page inert as it arrived (#485), or
// always when the caller says it is owed. It holds no live ROLE: the E2E
// suite reads a hidden alert or status as "nothing is wrong", so a permanent
// one would answer every such query.
export function ShellNotice({ message, severity, action, onClose, alertRef, spoken = message, owed = false }: {
  message: string | null;
  severity: AlertColor;
  action?: ReactNode;
  onClose?: () => void;
  // Makes the strip a focus target that is not a tab stop.
  alertRef?: Ref<HTMLDivElement>;
  spoken?: string | null;
  owed?: boolean;
}) {
  const { t } = useTranslation("common");
  const missed = useMissedAnnouncement(spoken, owed);
  const urgent = severity === "warning" || severity === "error";
  return (
    <>
      <p className="sr-only" aria-live={urgent ? "assertive" : "polite"} aria-atomic="true">{missed}</p>
      {message !== null && (
        <Alert severity={severity} className={urgent ? "farm-warning" : "shell-notice"} role={urgent ? "alert" : "status"}
          ref={alertRef} tabIndex={alertRef === undefined ? undefined : -1} action={action} onClose={onClose} closeText={t("close")}>
          {message}
        </Alert>
      )}
    </>
  );
}

// Each post is a new object, so the strip can tell one notice from the next.
interface Notice {
  message: string;
}

interface AppNotice {
  notice: Notice | null;
  show: (message: string) => void;
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
// session that ends, or another user's tokens, clears it. The owner is the
// user, not the credentials: a confirm or a password change keeps it.
export function AppNoticeProvider({ children }: { children: ReactNode }) {
  const { isAuthenticated, userId } = useAuth();
  const owner = isAuthenticated ? userId : null;
  // ponytail: one notice at a time, a new one replaces it, and it stays until
  // closed. A queue goes here once two notices can matter at the same time.
  const [notice, setNotice] = useState<Notice | null>(null);
  // Reset during render, not derived from an owner stored with the notice: a
  // sign-out and the same user's sign-in would otherwise bring it back.
  const [heldFor, setHeldFor] = useState(owner);
  if (heldFor !== owner) {
    setHeldFor(owner);
    setNotice(null);
  }
  const show = useCallback((message: string) => setNotice({ message }), []);
  const dismiss = useCallback(() => setNotice(null), []);
  const value = useMemo(() => ({ notice, show, dismiss }), [notice, show, dismiss]);
  return <AppNoticeContext.Provider value={value}>{children}</AppNoticeContext.Provider>;
}

// The posted notice, in the shell's strip. One that arrives while the strip
// is up takes focus once, when no dialog is open: that brings it into view and
// is how a screen reader hears it, so the offscreen region stays quiet. One
// the strip mounts with (posted on the forced setup screen) keeps the focus
// that entering the app set, and the region says it instead. Closing puts
// focus on the content, since the button that had it is gone.
export function PostedNotice() {
  const { notice, dismiss } = useAppNotice();
  const alert = useRef<HTMLDivElement>(null);
  const [mountedWith] = useState(notice);
  const handled = useRef(notice);
  const [blocked, setBlocked] = useState(anyDialogOpen);
  useEffect(() => onModalStateChange(setBlocked), []);
  useEffect(() => {
    if (notice === null || notice === handled.current || blocked) return;
    handled.current = notice;
    alert.current?.focus();
  }, [notice, blocked]);
  const close = () => {
    dismiss();
    document.getElementById(MAIN_CONTENT_ID)?.focus();
  };
  return (
    <ShellNotice message={notice?.message ?? null} severity="info" alertRef={alert}
      spoken={notice !== null && notice === mountedWith ? notice.message : null} owed onClose={close} />
  );
}
