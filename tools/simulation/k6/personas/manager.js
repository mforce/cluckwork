// #243 Task 6 — Manager persona: weighted iteration loop.
//
// The issue describes Manager qualitatively ("daily-entry review/adjust/
// flock ops -> model as reads of daily-entries/flocks/stock + the occasional
// draft order; keep constrained writes light"), not as an exact percentage
// table like the other four personas. Weights below turn that into numbers:
// history (flocks+grades+daily-entries — the review/flock-ops screen) is the
// biggest slice, stock and reports next (a Manager passes AdminOnly, so the
// money reports render too), a light dashboard glance, and the sales draft-
// order path kept deliberately small ("occasional").

import { dashboard, history, makeIdemKeyFactory, reports, salesBundle, stock, thinkSleep, weightedPick } from '../bundles.js';
import { maybeRefresh } from '../auth.js';

export const PERSONA = 'Manager';

export const WEIGHTS = [
  ['history', 30],
  ['stock', 20],
  ['reports', 20],
  ['salesBundle', 15],
  ['dashboard', 15],
];

/** One weighted iteration for a Manager VU. Refreshes the session first. */
export function iterate(session) {
  session = maybeRefresh(session);
  const idemKeyFn = makeIdemKeyFactory(PERSONA, 'capacity');

  switch (weightedPick(WEIGHTS)) {
    case 'history':
      history(session, PERSONA);
      break;
    case 'stock':
      stock(session, PERSONA);
      break;
    case 'reports':
      reports(session, PERSONA);
      break;
    case 'salesBundle':
      salesBundle(session, PERSONA, idemKeyFn);
      break;
    case 'dashboard':
      dashboard(session, PERSONA);
      break;
    default:
      break;
  }

  thinkSleep();
  return session;
}
