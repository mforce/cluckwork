# UI Contract: Async Named Entity Picker

## Public Surface

The generic engine is internal to the feature. Pages consume only typed adapters.

### Shared Snapshot

```text
PickerSnapshot<T>
  committed: T | null
  selectionPhase: uninitialized | resolving | committed | blank | unavailable
  exploring: boolean
  canSubmit: boolean
```

Every page receives snapshot changes and uses both `canSubmit` and a submit-handler guard. A disabled button alone is not the write-safety boundary.

### FlockPicker

Caller inputs:

- stable control ID and visible translated label;
- `eligibility`: Active, Active + Depleted, or All;
- required/optional and disabled state;
- explicit selection transition containing a stable page-owned generation key and one of: blank, exact ID, first Active, or first Active then first Depleted;
- translated page-specific unavailable message where needed.

Outputs:

- full committed `Flock`, including `farmId`, `houseId`, name, and status;
- `PickerSnapshot<Flock>` safety state.

Fixed adapter policy: 50-row pages and approximately 250 ms debounce.

### CustomerPicker

Caller inputs mirror FlockPicker except there is no eligibility choice. The transition may request blank, exact ID, or first customer.

Outputs: full committed `Customer` and `PickerSnapshot<Customer>`.

Fixed adapter policy: 50-row pages and approximately 250 ms debounce.

## Discovery Contract

- Opening or focusing the picker may discover the unfiltered first page.
- Raw text/eligibility change immediately advances discovery generation, clears old rows/errors, and enters debounce.
- Replacement success replaces rows; replacement failure exposes no old-query rows.
- Extension success appends unseen IDs in server order and advances offset by the raw server count.
- Extension failure retains existing rows and exposes adjacent Retry.
- Only current generation may change rows, loading, errors, cursor, or `hasMore`, including catch/finally.
- A full 50-row last page may require one empty extension to learn that paging is complete.

## Selection and Exploration Contract

- Committed typed entity and visible search text are independent.
- Typing text different from the committed label sets `exploring=true`; the old ID cannot be submitted.
- Arrow movement changes only active option.
- Enter or pointer activation commits the active entity and restores its label.
- Escape cancels exploration and restores the previous committed label/entity.
- Clearing is available only for optional pickers and commits blank.
- Exact/default/lifecycle/create/user transitions each advance the selection generation; stale completion has no effect.
- Missing, inaccessible, or ineligible exact identities enter `unavailable`; they never become the first result.
- A pointer interaction outside the picker may cancel exploration, but if its target is a write control that same interaction is suppressed. Form handlers independently reject `canSubmit=false`.
- Post-create recovery keeps the created ID and retries only exact GET hydration.

## Keyboard Contract

| Key/action | Behavior |
|---|---|
| Printable/editing keys | Native input editing; update raw query |
| Down Arrow | Activate next option; at loaded end with more results, request extension |
| Up Arrow | Activate previous option when available |
| Enter | Commit active option; no active option means no stale commit |
| Escape | Restore committed label/entity and close results |
| Home/End | Native input behavior; not intercepted for list navigation |
| Pointer option click | Same commit semantics as Enter |
| Retry activation | Retry failed operation and return focus to input |

## Accessibility Contract

- Visible `<label>` is associated with an editable input having `role="combobox"` and `aria-autocomplete="list"`.
- Input reports `aria-expanded` and stable `aria-controls` for the popup listbox.
- Popup has `role="listbox"`; every option has stable ID, `role="option"`, and accurate `aria-selected`.
- DOM focus remains on the input; `aria-activedescendant` identifies the active option.
- A stable mounted polite/atomic live region announces loading and result counts.
- Empty, replacement error, extension error, Retry, and Load more states are visible and translated.
- Retry is adjacent and keyboard reachable and restores input focus.
- Required and disabled state are expressed natively and through appropriate ARIA semantics.
- No custom portal or modal behavior is introduced.

## Styling and Localization

- Use existing form, focus, semantic-color, and responsive tokens in `web/src/styles.css`.
- Shared state strings live in a translated shared namespace with strict en/es/tl parity.
- Adapter/page labels and unavailable explanations remain page-specific translations.
- No hardcoded English in tests or the real-browser suite; selectors use catalog helpers.
