namespace Cluckwork.Domain.Eggs;

// #396 — which Daily Entry input feeds a grade. `Manual` is every ordinary
// grade, entered by hand in the Grading pane. `Cracked` and `Dirty` are the two
// grades fed by the entry's own condition counters instead, and are excluded
// from manual grading precisely so a condition egg cannot also be counted as a
// manual line (which would produce two lots for one grade on one day).
//
// Deliberately separate from EggGradeType: that says what KIND of bucket this
// is (a size, a quality, a custom one) and is the farm's own taxonomy, while
// this says WHERE THE NUMBER COMES FROM and is the app's wiring. A farm can
// have many Quality grades; only one of them can be the Cracked counter's.
[ModuleContract("EggOperations")]
public enum DailyEntryKind { Manual, Cracked, Dirty }
