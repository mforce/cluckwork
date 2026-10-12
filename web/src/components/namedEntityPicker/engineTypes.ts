// #1215 file split — the engine's internal state shape, initial state and
// props contract, moved verbatim out of NamedEntityPicker.tsx. Not re-exported:
// none of this is public API.

import type { ReactNode } from "react";
import type { DiscoveryState, FlockEligibilityKey, PickerSelection, PickerSnapshot } from "./types";

type NamedEntity = { id: string; name: string };

/** The discovery/selection state the engine owns; one object per render. */
interface EngineState<T extends NamedEntity> {
  discovery: DiscoveryState<T>;
  selection: PickerSelection<T>;
}

function initialState<T extends NamedEntity>(eligibilityKey: FlockEligibilityKey | null): EngineState<T> {
  return {
    discovery: {
      rawQuery: "",
      normalizedQuery: null,
      eligibilityKey,
      items: [],
      cursor: 0,
      hasMore: false,
      phase: "closed",
      error: null,
      discoveryGeneration: 0,
    },
    selection: { entity: null, requestedId: null, phase: "uninitialized", transitionGeneration: 0 },
  };
}

interface EngineProps<T extends NamedEntity> {
  /** Stable control id for the input (the label's htmlFor). */
  id: string;
  /** Visible, translated label text (page-owned translation). */
  label: string;
  /**
   * The page's trigger control (its native selector). Rendered in the CLOSED
   * state only (one field-sized control in the normal form slot). While open,
   * the trigger is absent and the combobox/listbox occupies the same position.
   */
  trigger: ReactNode;
  /**
   * The adapter's discovery fetch: one 50-row window. The engine supplies the
   * offset and the fixed page size; search/eligibility ride the adapter's
   * own typed parameters.
   */
  fetchPage: (search: string | null, offset: number) => Promise<T[]>;
  /**
   * US3 (T035): the adapter's EXACT-identity read. When the page passes a
   * full typed entity via `controlledCommitted` that is not already known to
   * the discovery window, the engine resolves it through THIS read (a GET),
   * never a list lookup — so a row-owned or late-sorting identity that is not
   * in the first 50 results still commits its exact entity. A scoped 404 /
   * transport failure enters the `unavailable` phase (never a first-result
   * substitution). Omitted for adapters that only ever commit from discovery.
   */
  fetchExact?: (id: string) => Promise<T>;
  /**
   * The adapter-owned eligibility policy. When it changes while the picker is
   * open, the engine invalidates old work and re-discovers under the new key.
   * Customers pass null.
   */
  eligibilityKey: FlockEligibilityKey | null;
  required?: boolean;
  disabled?: boolean;
  open?: boolean;
  onSnapshot: (snapshot: PickerSnapshot<T>) => void;
  /** US2: called on Escape to close the picker (page-controlled open state). */
  onEscape?: () => void;
  /** US2: called when the user clicks outside the picker (cancels exploration + close). */
  onOutsideClick?: () => void;
  /**
   * US2: page-controlled committed entity. When the page externally resets
   * its selection (deep link, URL change, dialog reset), it passes a new
   * entity (or null) here. The engine synchronizes its internal committed
   * state to match, so a later Escape or exploration cannot resurrect a
   * stale ID. `null` clears the committed state (optional pickers).
   */
  controlledCommitted?: T | null;
  /** Signals a controlled change (increment to re-trigger the sync effect). */
  controlledGeneration?: number;
  /**
   * US3 (T038): a row-owned identity the page must resolve but does not yet
   * have the full entity for (it is not in the page's capped list). The engine
   * resolves it through `fetchExact` on open / on the controlled sync — a
   * scoped 404 / transport failure enters `unavailable` (never a first-result
   * substitution). Omitted when the page already holds the full entity (use
   * `controlledCommitted` instead).
   */
  requestedId?: string | null;
  /**
   * US2: fires ONLY on genuine user commit (Enter or pointer click on an
   * option). This is the ONLY signal that should close the picker and trigger
   * page business side-effects (retarget, URL update, ID write). Distinct
   * from onSnapshot which fires on every state change for safety state.
   */
  onCommit?: (entity: T) => void;
  /** US2: fires when an optional picker is cleared (commits blank). */
  onClear?: () => void;
  /**
   * #916 — content pinned ABOVE the scrolling results, rendered as a sibling
   * of `<ul role="listbox">` inside the same `slots.paper` wrapper the
   * Load-more/status footer already uses (never inside `slotProps.listbox`,
   * where ARIA only allows `option`/`group`). Omitted by every caller except
   * one that needs a fixed "no filter" choice above the list; `Autocomplete`'s
   * own arrow-key navigation only ever traverses `options`, so this content
   * is Tab-reachable, not arrow-reachable.
   */
  pinnedChoice?: ReactNode;
}

export type { NamedEntity, EngineState };
export { initialState };
export type { EngineProps };
