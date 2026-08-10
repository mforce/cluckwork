import { useEffect, useRef, useState } from "react";
import { anyDialogOpen, onModalStateChange } from "./Dialog";

/**
 * Text for an offscreen live region that covers the announcements a visible
 * one could not make, because #483 had made it `inert` at the moment it had
 * something to say (#485).
 *
 * A banner like the update prompt or the farm-load warning carries its own
 * `role="status"` / `role="alert"`, and on an ordinary page that works: the
 * element is inserted with its message and the screen reader says it. But
 * everything outside the topmost dialog is inert, which takes it out of the
 * accessibility tree entirely — so a message that arrives while a dialog is
 * open is never announced, and un-inerting the banner afterwards replays
 * nothing, because nothing about it changed on the way out.
 *
 * This hook covers exactly that gap, and deliberately nothing else:
 *
 * - It stays EMPTY on the ordinary path, where the visible banner announces
 *   itself. Two live regions holding one sentence would say it twice.
 * - It returns a message only when that message appeared (or changed) while
 *   the page was inert, and only once the page is the user's own again.
 * - It starts empty and is only ever written from an effect, so the element is
 *   in the accessibility tree BEFORE it has anything to say. A live region
 *   inserted with its text already inside is not reliably announced —
 *   [W3C ARIA22](https://www.w3.org/WAI/WCAG21/Techniques/aria/ARIA22) wants
 *   the container present first, and Safari/VoiceOver drops the populated
 *   case. This matters in production, not just in theory: `SessionProvider`
 *   gates the shell on `/account` settling, so a failed read mounts
 *   `AppLayout` with its warning already true (codex review of #499).
 * - Once delivered it KEEPS the text rather than blanking, so a later dialog
 *   opening and closing over an unchanged message says nothing a second time.
 *
 * Pass `null` when there is nothing to say.
 */
export function useMissedAnnouncement(message: string | null): string {
  const [blocked, setBlocked] = useState(anyDialogOpen);
  useEffect(() => onModalStateChange(setBlocked), []);

  // The message the visible region could not speak for itself.
  const [missed, setMissed] = useState<string | null>(null);
  const previous = useRef<string | null>(null);

  useEffect(() => {
    if (message === previous.current) return;
    previous.current = message;
    // Every change reassigns the debt from scratch, rather than only ever
    // taking one on. A change the visible region CAN announce for itself
    // cancels whatever was owed; only one it cannot becomes the new debt.
    //
    // Leaving a settled debt behind is not harmless bookkeeping, because the
    // messages here are fixed strings: dismiss an update banner and let the
    // next one raise the same sentence with no dialog in the way, and a stale
    // `missed` still matches it — so this region speaks in chorus with the
    // visible one, which is the exact duplicate it exists to prevent (codex
    // review of #499). Same trap via a message that changes away and back.
    setMissed(message !== null && blocked ? message : null);
  }, [message, blocked]);

  const [shown, setShown] = useState("");
  useEffect(() => {
    if (message === null) { setShown(""); return; }
    // Writing into an inert region spends the announcement on a moment nobody
    // hears; leave whatever is there until the page comes back.
    if (blocked) return;
    // Owed and now deliverable, or not owed at all — in which case this region
    // must be empty rather than echoing what the visible banner already said.
    setShown(missed === message ? message : "");
  }, [blocked, missed, message]);

  return shown;
}
