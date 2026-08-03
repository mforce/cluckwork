import { describe, expect, it } from "vitest";
import { armedState, gradingState } from "./grading";

const base = { totalEggs: 100, cracked: 2, dirty: 3, discarded: 5, gradesSum: 0 };

describe("gradingState", () => {
  it("subtracts the three loss buckets to get sellable", () => {
    const g = gradingState(base);
    expect(g.losses).toBe(10);
    expect(g.sellable).toBe(90);
    expect(g.remaining).toBe(90);
  });

  it("counts down while grading is short", () => {
    const g = gradingState({ ...base, gradesSum: 60 });
    expect(g.tone).toBe("");
    expect(g.count).toBe(30);
    expect(g.saysKey).toBe("leftToGrade");
    expect(g.reconciled).toBe(false);
  });

  // The boundary from both sides — one short, exact, one over — so a fixture
  // can't pass with an off-by-one comparison in either direction.
  it.each([
    [89, false, ""],
    [90, true, "done"],
    [91, false, "over"],
  ])("gradesSum %i → reconciled %s, tone %s", (gradesSum, reconciled, tone) => {
    const g = gradingState({ ...base, gradesSum });
    expect(g.reconciled).toBe(reconciled);
    expect(g.tone).toBe(tone);
  });

  it("reports the overshoot as a positive count", () => {
    const g = gradingState({ ...base, gradesSum: 95 });
    expect(g.count).toBe(5);
    expect(g.saysKey).toBe("overSellableCount");
  });

  it("shows the sellable figure, not zero, once the day adds up", () => {
    const g = gradingState({ ...base, gradesSum: 90 });
    expect(g.count).toBe(90);
    expect(g.saysKey).toBe("gradedDayAddsUp");
  });

  // Losses past the total make `sellable` negative: "-5 left to grade" would be
  // a reading of a number that means nothing. The counts are the fix.
  it("reports broken counts instead of a derived remainder", () => {
    const g = gradingState({ totalEggs: 10, cracked: 8, dirty: 4, discarded: 3, gradesSum: 0 });
    expect(g.lossesExceedTotal).toBe(true);
    expect(g.tone).toBe("over");
    expect(g.count).toBeNull();
    expect(g.saysKey).toBe("fixCountsFirst");
    expect(g.reconciled).toBe(false);
  });

  it("treats an empty day as reconciled", () => {
    const g = gradingState({ totalEggs: 0, cracked: 0, dirty: 0, discarded: 0, gradesSum: 0 });
    expect(g.reconciled).toBe(true);
    expect(g.tone).toBe("done");
  });
});

// The whole truth table, stated where no scheduler can get between the input
// and the answer. The screen-level test that observes the same invariant has to
// dodge React's effect flushing to see it; this one cannot rot that way, so if
// a future React ever makes that test pass vacuously, this still fails.
describe("armedState", () => {
  it.each([
    [true, true, true],
    [true, false, false],
    [false, true, false],
    [false, false, false],
  ])("assigning=%s canAssign=%s → %s", (assigning, canAssign, expected) => {
    expect(armedState(assigning, canAssign)).toBe(expected);
  });

  // The case the bug lived in, named: the flag survives the moment the day
  // stops having anything to assign, and the derivation is what ignores it.
  it("is not armed the instant there is nothing left to assign", () => {
    expect(armedState(true, false)).toBe(false);
  });
});
