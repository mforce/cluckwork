// #243 Task 6 — Sales persona: weighted iteration loop.
//
// Frequency table (from the issue): 35% orders (the Draft-order write path +
// listing), 25% customers, 20% payments -> READ customer balances (not new
// payments — confirming/paying stays in the deferred hazard passes), 20%
// stock.

import { customerBalances, customersBundle, makeIdemKeyFactory, salesBundle, stock, thinkSleep, weightedPick } from '../bundles.js';
import { maybeRefresh } from '../auth.js';

export const PERSONA = 'Sales';

export const WEIGHTS = [
  ['salesBundle', 35],
  ['customersBundle', 25],
  ['customerBalances', 20],
  ['stock', 20],
];

/** One weighted iteration for a Sales VU. Refreshes the session first. */
export function iterate(session) {
  session = maybeRefresh(session);
  const idemKeyFn = makeIdemKeyFactory(PERSONA, 'capacity');

  switch (weightedPick(WEIGHTS)) {
    case 'salesBundle':
      salesBundle(session, PERSONA, idemKeyFn);
      break;
    case 'customersBundle':
      customersBundle(session, PERSONA);
      break;
    case 'customerBalances':
      customerBalances(session, PERSONA);
      break;
    case 'stock':
      stock(session, PERSONA);
      break;
    default:
      break;
  }

  thinkSleep();
  return session;
}
