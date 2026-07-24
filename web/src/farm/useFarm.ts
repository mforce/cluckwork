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
// Inside a provider the value comes from the provider, which keeps it current
// as the farm's day rolls over, so a tab left open past farm-midnight starts
// offering the new day instead of capping at yesterday. Outside one — screens
// rendered on their own in tests — it is computed live, browser-local.
//
// A field that seeds its INITIAL value from this still holds whatever the day
// was when it mounted; a dialog that can outlive a rollover re-seeds when it
// opens (SalesPage).
export function useFarmToday(): string {
  const { farm, today } = useFarm();
  return today ?? todayIso(farm?.timeZoneId);
}
