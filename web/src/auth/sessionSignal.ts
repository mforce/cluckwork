export const SESSION_CHANNEL = "cluckwork-session";

export function announceSessionChange(): void {}

export function onSessionChangeElsewhere(_listener: () => void): () => void {
  return () => undefined;
}
