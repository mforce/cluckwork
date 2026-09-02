import { useCallback, useId } from "react";
import type { ReactNode } from "react";
import { NamedEntityPickerEngine } from "./NamedEntityPicker";
import type { FlockEligibilityKey, PickerSnapshot } from "./NamedEntityPicker";
import { listFlocks, getFlock } from "../api/cluckwork";
import type { Flock, FlockEligibility } from "../api/cluckwork";

// #512 — the typed flock adapter over the shared engine (T015). Pages consume
// ONLY this; the generic engine stays feature-local (FR-053 forbids a public
// catalog API). The adapter fixes the policy — 50-row pages, the engine's
// 250 ms debounce — and maps the flock transport (search, eligibility, offset).

type Trigger = ReactNode;

/**
 * Maps the adapter's `FlockEligibility` prop to the engine's
 * `FlockEligibilityKey`. An exhaustive compile-time mapping: if the two unions
 * diverge, this fails to compile rather than silently casting.
 */
const ELIGIBILITY_TO_KEY: Record<FlockEligibility, FlockEligibilityKey> = {
  active: "active",
  "active-and-depleted": "active-and-depleted",
  all: "all",
};
function toEligibilityKey(eligibility: FlockEligibility): FlockEligibilityKey {
  return ELIGIBILITY_TO_KEY[eligibility];
}

export function FlockPicker({ label, eligibility = "active-and-depleted", required = false, disabled = false, open = false, trigger, onSnapshot, onEscape, onOutsideClick, controlledCommitted, controlledGeneration, requestedId, onCommit, onClear }: {
  label: string;
  /** The three policies from data-model.md; omitted keeps today's behaviour. */
  eligibility?: FlockEligibility;
  required?: boolean;
  disabled?: boolean;
  /**
   * Whether the combobox is open. Defaults to `false` — the page controls
   * when the picker opens (T016–T018 route adoption).
   */
  open?: boolean;
  trigger?: Trigger;
  onSnapshot?: (snapshot: PickerSnapshot<Flock>) => void;
  /** US2: close on Escape. */
  onEscape?: () => void;
  /** US2: close on outside-click. */
  onOutsideClick?: () => void;
  /** US2: page-controlled committed entity (external reset synchronization). */
  controlledCommitted?: Flock | null;
  /** US2: signals a controlled change. */
  controlledGeneration?: number;
  /** US2: fires ONLY on genuine user commit (Enter/pointer). */
  onCommit?: (entity: Flock) => void;
  /** US2: fires when an optional picker is cleared. */
  onClear?: () => void;
  /** US3 (T038): a row-owned ID to resolve via the exact GET (not in the capped list). */
  requestedId?: string | null;
}) {
  const id = useId();
  const eligibilityKey = toEligibilityKey(eligibility);
  // US3 (T035): exact-identity read — resolves a row-owned / late-sorting
  // identity not in the discovery window, and surfaces a scoped 404 as
  // `unavailable` (never a first-result substitution).
  const fetchExact = useCallback((fid: string) => getFlock(fid), []);
  const fetchPage = useCallback(
    (search: string | null, offset: number) =>
      listFlocks({
        search: search ?? undefined,
        eligibility,
        limit: 50,
        offset,
      }),
    [eligibility],
  );
  return (
    <NamedEntityPickerEngine<Flock>
      id={id}
      label={label}
      trigger={trigger}
      fetchPage={fetchPage}
      fetchExact={fetchExact}
      eligibilityKey={eligibilityKey}
      required={required}
      disabled={disabled}
      open={open}
      onSnapshot={onSnapshot ?? (() => {})}
      onEscape={onEscape}
      onOutsideClick={onOutsideClick}
      controlledCommitted={controlledCommitted}
      controlledGeneration={controlledGeneration}
      requestedId={requestedId}
      onCommit={onCommit}
      onClear={onClear}
    />
  );
}
