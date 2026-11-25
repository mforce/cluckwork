import { useState } from "react";
import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { NO_PROOF } from "../../api/client";
import type { StepUpProof } from "../../api/client";
import type { User } from "../../api/cluckwork";
import { clearAccessToken, clearBoundAccount, setAccessToken } from "../../auth/tokenStore";
import { useDialogAction } from "../../components/useDialogAction";
import i18n from "../../i18n";
import { useChangeEmail } from "./useChangeEmail";

// #1284 (r2, Opus and Fable) — the change-email write carries a step-up grant,
// so a 401 Auth.CredentialsSuperseded on it comes back unsent again
// (refresh.ts isUnresendable). On the actor's own row the dialog still says
// why nothing changed; only the list read after a successful change stays
// silent, because that session is ending. The real API layer runs; only
// stepUp is stubbed.
vi.mock("../../api/client", async (orig) => ({
  ...(await orig<typeof import("../../api/client")>()),
  stepUp: vi.fn(async () => ({ token: crypto.randomUUID(), expiresAt: "2099-01-01T00:05:00Z" })),
}));

const json = (body: unknown, status: number) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/problem+json" } });
const b64url = (s: string) => btoa(s).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
const tok = () => `h.${b64url(JSON.stringify({ sub: "self", account_id: "acct-A", exp: 9999999999 }))}.s`;

const SELF: User = {
  id: "self", email: "self@farm.test", displayName: null, role: "Owner",
  disabledAt: null, twoFactorEnabled: false, passkeyCount: 0,
};

let fetchMock: ReturnType<typeof vi.fn>;
beforeEach(() => {
  fetchMock = vi.fn();
  vi.stubGlobal("fetch", fetchMock);
  setAccessToken(tok());
  clearBoundAccount();
});
afterEach(() => {
  vi.unstubAllGlobals();
  clearAccessToken();
  clearBoundAccount();
});

function render() {
  return renderHook(() => {
    const dialog = useDialogAction(["change-email"]);
    const [emailUser, setEmailUser] = useState<User | null>(null);
    const [emailValue, setEmailValue] = useState("");
    const [proof, setProof] = useState<StepUpProof>(NO_PROOF);
    const [, setFieldError] = useState<string | null>(null);
    const [message, setMessage] = useState<string | null>(null);
    const h = useChangeEmail({
      dialog, setUsers: vi.fn(), keyFor: () => "key-1", clearKey: vi.fn(), setMessage, myId: "self",
      emailUser, setEmailUser, emailValue, setEmailValue,
      emailStepUpProof: proof, setEmailStepUpProof: setProof, setEmailFieldError: setFieldError,
    });
    return { h, dialog, emailUser, message, setEmailValue, setProof };
  });
}

it("says why nothing changed when the actor's own email change is refused as superseded", async () => {
  fetchMock
    .mockResolvedValueOnce(json({ title: "Auth.CredentialsSuperseded", detail: "Your credentials changed." }, 401))

  const { result } = render();
  act(() => result.current.h.openEmail(SELF));
  act(() => result.current.setEmailValue("new-self@farm.test"));
  act(() => result.current.setProof({ password: crypto.randomUUID() }));
  await act(async () => { await result.current.h.onChangeEmail({ preventDefault() {} } as never); });

  expect(fetchMock).toHaveBeenCalledTimes(1);
  expect(result.current.emailUser).not.toBeNull();
  expect(result.current.dialog.errors.forDialog("change-email")).toBe(i18n.t("errors:Auth.CredentialsSuperseded"));
});

// r3 (Fable E4, E5) — after a successful own change, only a superseded read
// stays silent, and it neither reports success nor closes the dialog.
async function changeOwnEmail() {
  const view = render();
  act(() => view.result.current.h.openEmail(SELF));
  act(() => view.result.current.setEmailValue("new-self@farm.test"));
  act(() => view.result.current.setProof({ password: crypto.randomUUID() }));
  await act(async () => { await view.result.current.h.onChangeEmail({ preventDefault() {} } as never); });
  return view.result;
}

it("reports any other failure of the list read after an own change", async () => {
  fetchMock
    .mockResolvedValueOnce(new Response(null, { status: 204 }))
    .mockResolvedValueOnce(json({ title: "Server error", detail: "boom" }, 500));

  const result = await changeOwnEmail();

  expect(result.current.dialog.errors.forDialog("change-email")).toBe("boom");
});

it("neither reports success nor closes the dialog when the read after an own change is refused as superseded", async () => {
  fetchMock
    .mockResolvedValueOnce(new Response(null, { status: 204 }))
    .mockResolvedValueOnce(json({ title: "Auth.CredentialsSuperseded", detail: "Your credentials changed." }, 401))
    .mockResolvedValueOnce(json({ title: "Identity.InvalidRefreshToken", detail: "The refresh token is invalid." }, 401));

  const result = await changeOwnEmail();

  expect(result.current.message).toBeNull();
  expect(result.current.emailUser).not.toBeNull();
  expect(result.current.dialog.errors.forDialog("change-email")).toBeUndefined();
});
