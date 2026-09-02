import { useCallback, useId } from "react";
import type { ReactNode } from "react";
import { NamedEntityPickerEngine } from "./NamedEntityPicker";
import type { PickerSnapshot } from "./NamedEntityPicker";
import { listCustomers, getCustomer } from "../api/cluckwork";
import type { Customer } from "../api/cluckwork";

// #512 — the typed customer adapter over the shared engine (T015). Pages
// consume ONLY this; the generic engine stays feature-local (FR-053).
// Customers have no eligibility choice: the engine's eligibilityKey is null.

type Trigger = ReactNode;

export function CustomerPicker({ label, required = false, disabled = false, open = false, trigger, onSnapshot, onEscape, onOutsideClick, controlledCommitted, controlledGeneration, requestedId, onCommit, onClear }: {
  label: string;
  required?: boolean;
  disabled?: boolean;
  /**
   * Whether the combobox is open. Defaults to `false` — the page controls
   * when the picker opens (T016–T018 route adoption).
   */
  open?: boolean;
  trigger?: Trigger;
  onSnapshot?: (snapshot: PickerSnapshot<Customer>) => void;
  /** US2: close on Escape. */
  onEscape?: () => void;
  /** US2: close on outside-click. */
  onOutsideClick?: () => void;
  /** US2: page-controlled committed entity (external reset synchronization). */
  controlledCommitted?: Customer | null;
  /** US2: signals a controlled change. */
  controlledGeneration?: number;
  /** US3 (T038): a row-owned ID to resolve via the exact GET (not in the capped list). */
  requestedId?: string | null;
  /** US2: fires ONLY on genuine user commit (Enter/pointer). */
  onCommit?: (entity: Customer) => void;
  /** US2: fires when an optional picker is cleared. */
  onClear?: () => void;
}) {
  const id = useId();
  // US3 (T035): exact-identity read (same contract as FlockPicker).
  const fetchExact = useCallback((cid: string) => getCustomer(cid), []);
  const fetchPage = useCallback(
    (search: string | null, offset: number) =>
      listCustomers({ search: search ?? undefined, limit: 50, offset }),
    [],
  );
  return (
    <NamedEntityPickerEngine<Customer>
      id={id}
      label={label}
      trigger={trigger}
      fetchPage={fetchPage}
      fetchExact={fetchExact}
      eligibilityKey={null}
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
