// #243 Task 6 — Owner/Admin persona: weighted iteration loop.
//
// The issue describes Owner/Admin qualitatively ("dashboard, reports, audit
// browse, one /export occasionally (heavy), user-management read"). Weights
// below: dashboard and reports dominate, audit and a light stock glance next,
// user-management reads a modest slice, and /export/all (a whole-account CSV
// zip, streamed) kept deliberately rare — "occasionally (heavy)".

import { auditBundle, dashboard, exportBackup, reports, stock, thinkSleep, usersRead, weightedPick } from '../bundles.js';
import { maybeRefresh } from '../auth.js';

export const PERSONA = 'Owner';

export const WEIGHTS = [
  ['dashboard', 35],
  ['reports', 25],
  ['auditBundle', 15],
  ['usersRead', 10],
  ['stock', 10],
  ['exportBackup', 5],
];

/** One weighted iteration for an Owner VU. Refreshes the session first. */
export function iterate(session) {
  session = maybeRefresh(session);

  switch (weightedPick(WEIGHTS)) {
    case 'dashboard':
      dashboard(session, PERSONA);
      break;
    case 'reports':
      reports(session, PERSONA);
      break;
    case 'auditBundle':
      auditBundle(session, PERSONA);
      break;
    case 'usersRead':
      usersRead(session, PERSONA);
      break;
    case 'stock':
      stock(session, PERSONA);
      break;
    case 'exportBackup':
      exportBackup(session, PERSONA);
      break;
    default:
      break;
  }

  thinkSleep();
  return session;
}
