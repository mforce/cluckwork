import { act } from "@testing-library/react";
import { SESSION_CHANNEL } from "../auth/sessionSignal";

// #1398 — another tab signs in or out, as its own channel posts it. The
// message arrives on a later task, so the wait for it is inside act, and the
// update it causes is flushed there too.
export async function otherTabSignals(): Promise<void> {
  const otherTab = new BroadcastChannel(SESSION_CHANNEL);
  const probe = new BroadcastChannel(SESSION_CHANNEL);
  const heard = new Promise<void>((resolve) => { probe.onmessage = () => resolve(); });
  await act(async () => {
    otherTab.postMessage(null);
    await heard;
  });
  otherTab.close();
  probe.close();
}
