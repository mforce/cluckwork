# Combined direction: candidate 1's dashboard bones in candidate 2's colour and type

One sentence: the dashboard keeps candidate 1's structure and controls (Today rows with recorded times and the amber full-row rule on the missing house, sales ledger with the order number under the customer, Eggs on hand and Last 14 days in a right column, an attention line that folds into a count) and everything it wears comes from candidate 2, whose daily entry and phone shell are used unchanged.

## From candidate 1 (structure, controls)
- Today is a ruled list: name, entry state with its time, action, count; the unrecorded house carries a 3px warn rule down its left edge; a double rule closes the total.
- A row action is ruled text at 1280 and a full width 44px outlined button at 390. One affordance, two densities.
- Two columns at 1280: Today and Recent sales left, Eggs on hand and Last 14 days in a 320px right column across a hairline.
- Recent sales rows: customer with the order number under it, eggs and grade, peso amount, status, action.
- The attention line never wraps: at 1280 two items fit and the rest fold into "+N more"; at 390 one item fits.
- At 390 Today is a ruled list: name with the entry state under it, count right at the row numeral size; the unrecorded house is a full width `--tint-warn` band with a `--warn` left rule and a 48px "Record House C" button inside; "Continue House B" is ruled text on its row. Switch: `<html data-variant="tiles">` or `?variant=tiles` renders the alternative, compact 2x2 tiles with no buttons where the whole tile is the tap target and the missing house is tinted.

## From candidate 2 (colour, type, rules, daily entry, phone shell)
- Nav rail is `--lavender` tinted paper with `--stat-accent` text, not a brand slab. Brand appears in four places: farm name, active nav item (2px rule plus text), primary button, focus ring.
- Status is a word with an 8px dot: success (recorded, paid), `--stat-accent` (allocated), hollow ring (draft), `--warn` (not recorded, low). No badge fills. Grade hues only in the stock meter and swatches. Links are ink, underlined in a 28% ink rule.
- Type: display numeral 40/44 weight 600 (one per section: eggs on hand, hen-day), title 24/28 (phone 28/32), section head 13/16 ruled, rows 14/20 desktop and 16/24 phone with weight 500 tabular numerals, caption 12/16, the nav group divider the only uppercase.
- Two rule weights: `--rule` between rows, `--rule-strong` (28% ink) under headers, above totals, under fields. Controls 4px radius, cards 8px, dialogs 12px. Elevation only on the action bar, the tab bar and dialogs.
- Desktop rows 36px; phone rows 52px minimum, every target 44px or more, buttons 48px, the phone tab bar with icons and the phone daily entry exactly as candidate 2 built them.
- Last 14 days is candidate 1's chart as it rendered: bars on `--surface-2` tracks scaled from a floor of 800 (Peak 1,009 at the top), a 9px week break, a dashed ink average across the bars, the selected day ringed in `--focus` with its readout balloon in a reserved 26px row above the strip, Avg and Peak at the head, the caption row "1 Sep / scale from 800 / 14 Sep" under a hairline, and the hen-day figure with its delta below. The app's strip is zero-based today (#780); adopting this floor is a product decision for the slice, since a bar from 800 makes an 880 day look like a 40% day.

## Rejected
- Candidate 1's aubergine rail slab and blue link colour.
- Candidate 2's original single-column Today table and its tile-as-link on the phone; candidate 1's per-row control is clearer.
- Material defaults: filled inputs, 12px radius everywhere, elevated app bar, badge chips.
