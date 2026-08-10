import { useEffect, useState } from "react";
import { anyDialogOpen, onModalStateChange } from "./Dialog";

/**
 * Text to render into a permanently-mounted, initially-empty live region, for
 * a message that has to survive being made `inert` (#485).
 *
 * Two separate things break the obvious approach of putting `role="status"` on
 * the visible banner and leaving it there:
 *
 * 1. A live region only speaks when its contents CHANGE while it is in the
 *    accessibility tree. #483 makes everything outside the topmost dialog
 *    `inert`, which takes the banner out of that tree entirely — so a message
 *    that arrives under a dialog is never announced, and un-inerting the
 *    banner later replays nothing, because nothing changed on the way out.
 * 2. Inserting a live region that ALREADY contains its message is not a
 *    reliable announcement in the first place — the container has to carry the
 *    role before the text lands in it (W3C ARIA22), and Safari/VoiceOver in
 *    particular drops the populated-on-insert case. So swapping in a fresh
 *    node full of text fixes nothing for exactly the users this is for.
 *
 * Hence: the region stays mounted and empty, and the message is written INTO
 * it once the page is the user's own again. Blanking it whenever a dialog
 * opens is what makes the next write a genuine change rather than a no-op
 * re-render — and it costs nothing, because a region nobody can reach is a
 * region nobody can hear.
 *
 * Pass `null` when there is nothing to say.
 */
export function useLiveAnnouncement(message: string | null): string {
  // Seeded from the live stack, not `false`: this hook can mount while a
  // dialog is already open, and starting out unblocked would render the
  // message into an inert region and then never change it again.
  const [blocked, setBlocked] = useState(anyDialogOpen);

  useEffect(() => onModalStateChange(setBlocked), []);

  return blocked || message === null ? "" : message;
}
