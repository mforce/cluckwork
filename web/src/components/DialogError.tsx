import type { DialogErrors } from "./useDialogErrors";

/**
 * #479 — the message a dialog raised, rendered inside that dialog.
 *
 * Twenty-two of these across eleven screens. The markup lives here rather than
 * at each site for two reasons: a site that forgets it is a MISSING element,
 * which a test names, instead of a subtly wrong one; and `role="alert"` is
 * decided once (SalesPage carried it, StockPage did not).
 *
 * An empty slot renders nothing at all — not an empty region — so a screen
 * reader announces the failure when it arrives and finds nothing when there is
 * nothing to find.
 */
export function DialogError({ errors, scope }: { errors: DialogErrors; scope: string }) {
  const text = errors.forDialog(scope);
  if (!text) return null;
  return <p className="error" role="alert">{text}</p>;
}
