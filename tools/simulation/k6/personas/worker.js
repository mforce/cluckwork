// #243 Task 6 — Worker persona: weighted iteration loop.
//
// Frequency table (from the issue): ~60% daily-entry save/read (mostly READ
// of daily-entries + stock; the actual save is rare and tolerant — see
// bundles.dailyEntryScreen), 15% stock, 15% history, 10% misc (split here
// into a light sessionBootstrap and the expected-403 deepLinkProbe: a Worker
// has no AdminOnly/OwnerOnly access, so /audit /users /export 403 for it too
// — AuthPolicies.cs).

import {
  dailyEntryScreen, deepLinkProbe, history, makeIdemKeyFactory, sessionBootstrap,
  stock, thinkSleep, weightedPick,
} from '../bundles.js';
import { maybeRefresh } from '../auth.js';

export const PERSONA = 'Worker';

export const WEIGHTS = [
  ['dailyEntryScreen', 60],
  ['stock', 15],
  ['history', 15],
  ['sessionBootstrap', 6],
  ['deepLinkProbe', 4],
];

/** One weighted iteration for a Worker VU. Refreshes the session first. */
export function iterate(session) {
  session = maybeRefresh(session);
  const idemKeyFn = makeIdemKeyFactory(PERSONA);

  switch (weightedPick(WEIGHTS)) {
    case 'dailyEntryScreen':
      dailyEntryScreen(session, PERSONA, idemKeyFn);
      break;
    case 'stock':
      stock(session, PERSONA);
      break;
    case 'history':
      history(session, PERSONA);
      break;
    case 'sessionBootstrap':
      sessionBootstrap(session, PERSONA);
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
