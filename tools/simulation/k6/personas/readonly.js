// #243 Task 6 — ReadOnly persona: weighted iteration loop.
//
// Frequency table (from the issue): ~40% dashboard, 30% reports, 20% stock,
// 10% history, "+ a small slice of the expected-403 deep-links". Folded the
// deep-link slice into the 100% split (dashboard/reports/stock/history
// trimmed proportionally) rather than layering it on top, so the weights
// stay simple to reason about: dashboard 38, reports 27, stock 18, history 9,
// deepLinkProbe 8. /audit /users /export 403 for ReadOnly by design — no SPA
// nav entry, but the API still gates them (AuthPolicies.cs: ReadOnly has
// neither AdminOnly nor OwnerOnly nor SalesAccess/SalesFlow).

import { dashboard, deepLinkProbe, history, reports, stock, thinkSleep, weightedPick } from '../bundles.js';
import { maybeRefresh } from '../auth.js';

export const PERSONA = 'ReadOnly';

export const WEIGHTS = [
  ['dashboard', 38],
  ['reports', 27],
  ['stock', 18],
  ['history', 9],
  ['deepLinkProbe', 8],
];

/** One weighted iteration for a ReadOnly VU. Refreshes the session first. */
export function iterate(session) {
  session = maybeRefresh(session);

  switch (weightedPick(WEIGHTS)) {
    case 'dashboard':
      dashboard(session, PERSONA);
      break;
    case 'reports':
      reports(session, PERSONA);
      break;
    case 'stock':
      stock(session, PERSONA);
      break;
    case 'history':
      history(session, PERSONA);
      break;
    case 'deepLinkProbe':
      deepLinkProbe(session, PERSONA);
      break;
    default:
      break;
  }

  thinkSleep();
  return session;
}
