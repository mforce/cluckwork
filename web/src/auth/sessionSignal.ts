// #1398 — tells this browser's other tabs that a session here began or ended,
// so a tab still showing unsaved recovery codes drops them. The message is
// null: no token, code or identity. Any same-origin page can post one (the
// lesson of the channel #532 deleted), so a listener may only do what a
// forged message cannot harm. One channel per tab, which never hears its own
// posts. Where BroadcastChannel is missing nothing is sent or heard, and the
// codes stay until Done, as before.
export const SESSION_CHANNEL = "cluckwork-session";

let channel: BroadcastChannel | null | undefined;
const open = () => (channel ??= typeof BroadcastChannel === "undefined" ? null : new BroadcastChannel(SESSION_CHANNEL));

export function announceSessionChange(): void {
  open()?.postMessage(null);
}

export function onSessionChangeElsewhere(listener: () => void): () => void {
  const tab = open();
  if (!tab) return () => undefined;
  tab.addEventListener("message", listener);
  return () => tab.removeEventListener("message", listener);
}
