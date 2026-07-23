import { useContext } from "react";
import { FarmContext } from "./FarmContext";
import { todayIso } from "../lib/dates";

export function useFarm() {
  return useContext(FarmContext);
}

// Today in the FARM's timezone (#123). Every date input's `max` — and its
// initial value — goes through here, because since #35 the API judges "is this
// in the future?" against the farm's day, not the browser's.
//
// Read on every render rather than frozen in state, so a screen that re-renders
// after farm-midnight offers the new day. Nothing re-renders it on the clock
// alone — a screen sitting idle across midnight still shows the old `max` until
// something else moves.
export function useFarmToday(): string {
  return todayIso(useFarm().farm?.timeZoneId);
}
