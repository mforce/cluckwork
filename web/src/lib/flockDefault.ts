import { getFlock, listFlocks } from "../api/cluckwork";
import type { Flock } from "../api/cluckwork";
import { readAccountScoped, writeAccountScoped } from "./accountStorage";

// #646 — the capture screens' default flock, in one place.
//
// The three screens that pick a default used to scan the capped `listFlocks()`
// page they already held for other purposes. `GET /flocks` answers with
// DefaultPageSize = 100 ordered by NAME, so on a farm with more than 100
// flocks every default came from the first alphabetical page — and a farm
// whose first hundred names are all archived or depleted got no default at
// all, or the wrong one. Nothing was unreachable (the picker reaches every
// flock, #512), so this is a poor default rather than a lost flock.
//
// Order of preference, owner decision 2026-09-05:
//   1. the flock this user last recorded against (account-scoped, below);
//   2. the first ACTIVE flock by name — depleted ones are backfill targets you
//      pick deliberately, never a default;
//   3. the first depleted one, so a farm between flocks still opens on
//      something rather than an empty picker.
export const LAST_FLOCK_KEY = "cluckwork.lastFlockId";

export function readLastFlockId(): string | null {
  return readAccountScoped(LAST_FLOCK_KEY);
}

export function rememberFlockId(id: string): void {
  writeAccountScoped(LAST_FLOCK_KEY, id);
}

// The already-loaded page is consulted FIRST, deliberately: on a farm inside
// one page it holds the same answer the server would give (both are name
// ordered), so the common case costs no extra request and behaves exactly as
// before. The query below is the escape hatch for the case that motivated
// #646 — the answer not being on page one.
export async function resolveDefaultFlock(listed: readonly Flock[]): Promise<Flock | null> {
  const remembered = readLastFlockId();
  if (remembered) {
    // #699 review — the ARCHIVED check has to be on this path too, not only
    // after the exact GET below. Water lists with includeArchived: true, so
    // correcting an archived row leaves that flock both remembered and
    // present in `listed`, and the next reset would open a capture form on a
    // flock nothing can be recorded against.
    const onPage = listed.find((f) => f.id === remembered);
    if (onPage) {
      if (onPage.status !== "Archived") return onPage;
      // Known archived: the page already answered, so asking the server the
      // same question would only cost a round trip to reach the same "no".
    } else {
      // Remembered but off-page, or deleted since. An exact GET is the same
      // resolution the pickers use for an out-of-window id (#512); a failure
      // here is not an error to report, it just means "no memory".
      try {
        const exact = await getFlock(remembered);
        if (exact.status !== "Archived") return exact;
      } catch {
        // fall through to the active/depleted defaults
      }
    }
  }

  const activeOnPage = listed.find((f) => f.status === "Active");
  if (activeOnPage) return activeOnPage;

  // limit: 1 — the server orders by name, so one row IS "the first active
  // flock", with no page to fall off the end of.
  const [firstActive] = await listFlocks({ eligibility: "active", limit: 1 });
  if (firstActive) return firstActive;

  const depletedOnPage = listed.find((f) => f.status === "Depleted");
  if (depletedOnPage) return depletedOnPage;

  const [firstDepleted] = await listFlocks({ eligibility: "active-and-depleted", limit: 1 });
  return firstDepleted ?? null;
}
