import { useState } from "react";
import { act, renderHook } from "@testing-library/react";
import { expect, it, vi } from "vitest";
import { NO_PROOF } from "../../api/client";
import type { StepUpProof } from "../../api/client";
import { useDialogAction } from "../../components/useDialogAction";
import { useResetTwoFactor } from "./useResetTwoFactor";

// #1280 — the page tests see the proof only on reopen, which also clears it,
// so closing is pinned here on its own.
it("clears the proof the moment the dialog closes", () => {
  const { result } = renderHook(() => {
    const [proof, setProof] = useState<StepUpProof>(NO_PROOF);
    const reset = useResetTwoFactor({
      dialog: useDialogAction(["reset-two-factor"]),
      setUsers: vi.fn(), keyFor: () => "key", clearKey: vi.fn(), setMessage: vi.fn(),
      resetStepUpProof: proof, setResetStepUpProof: setProof,
    });
    return { proof, setProof, reset };
  });
  act(() => result.current.reset.openReset({
    id: "target", email: "target@farm.test", displayName: null, role: "Worker",
    disabledAt: null, twoFactorEnabled: true,
  }));
  act(() => result.current.setProof({ password: crypto.randomUUID(), code: crypto.randomUUID() }));
  act(() => result.current.reset.closeReset());

  expect(result.current.proof).toEqual(NO_PROOF);
});
