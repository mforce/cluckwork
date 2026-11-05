import { describe, it, expect, vi, beforeEach } from "vitest";
import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { AccountPage } from "../AccountPage";
import { renderWithProviders } from "../../test/renderWithProviders";
import { farmState } from "../../test/fixtures";
import { freshCode } from "../../test/codes";
import { AuthContext } from "../../auth/AuthContext";
import { FarmContext } from "../../farm/FarmContext";
import { MeContext } from "../../session/SessionContext";
import { getTwoFactorStatus, listMyConnectedApps, regenerateRecoveryCodes, startTwoFactor } from "../../api/cluckwork";
import type { TwoFactorStatus } from "../../api/cluckwork";
import { ApiError, confirmTwoFactor, stepUp, turnOffTwoFactor } from "../../api/client";
import i18n from "../../i18n";

// #1277 — the Account page's Security section. The network is mocked at the
// API seam; ApiError stays real, because the section branches on it.
vi.mock("../../api/cluckwork", async (importOriginal) => ({
  ...await importOriginal<typeof import("../../api/cluckwork")>(),
  getTwoFactorStatus: vi.fn(),
  startTwoFactor: vi.fn(),
  regenerateRecoveryCodes: vi.fn(),
  listMyConnectedApps: vi.fn(),
}));
vi.mock("../../api/client", async (importOriginal) => ({
  ...await importOriginal<typeof import("../../api/client")>(),
  stepUp: vi.fn(),
  confirmTwoFactor: vi.fn(),
  turnOffTwoFactor: vi.fn(),
  changePassword: vi.fn(),
  logout: vi.fn(),
  setOnTokensChanged: vi.fn(),
  setOnUnauthenticated: vi.fn(),
}));

const mockStatus = vi.mocked(getTwoFactorStatus);
const mockStart = vi.mocked(startTwoFactor);
const mockRegenerate = vi.mocked(regenerateRecoveryCodes);
const mockStepUp = vi.mocked(stepUp);
const mockConfirm = vi.mocked(confirmTwoFactor);
const mockTurnOff = vi.mocked(turnOffTwoFactor);

const EMAIL = "test@farm.local";
const OFF: TwoFactorStatus = { enabled: false, recoveryCodesLeft: 0 };
const ON: TwoFactorStatus = { enabled: true, recoveryCodesLeft: 10 };
const UNENROLLED = { sub: "u1", role: "Worker" };
const ENROLLED = { ...UNENROLLED, two_factor: "true" };
// Every key, URI, grant and code is made at run time; none is a literal.
const freshKey = () => crypto.randomUUID().replace(/-/g, "").toUpperCase().slice(0, 32);
const enrolmentFor = (key = freshKey()) => ({ key, otpauthUri: `otpauth://totp/Cluckwork:${EMAIL}?secret=${key}&issuer=Cluckwork` });
const freshCodes = () => Array.from({ length: 10 }, () => crypto.randomUUID().slice(0, 19));
const grant = () => ({ token: crypto.randomUUID(), expiresAt: "2099-01-01T00:00:00Z" });
const password = () => `Aa1!${crypto.randomUUID()}`;

const t = (key: string, options: Record<string, unknown> = {}): string => i18n.t(`account:${key}` as "account:heading", options);
const dialog = () => screen.getByRole("dialog");
const press = async (name: string | RegExp, within_: HTMLElement = document.body) => {
  await act(async () => { fireEvent.click(within(within_).getByRole("button", { name })); });
};
const passwordField = () => within(dialog()).getByLabelText(/Your current password/);
const codeField = () => within(dialog()).queryByLabelText(/Authenticator or recovery code/);
const prove = (enrolled: boolean) => {
  const proof = { password: password(), ...(enrolled && { code: freshCode() }) };
  fireEvent.change(passwordField(), { target: { value: proof.password } });
  if (proof.code) fireEvent.change(codeField()!, { target: { value: proof.code } });
  return proof;
};

beforeEach(() => {
  vi.resetAllMocks();
  vi.mocked(listMyConnectedApps).mockResolvedValue([]);
  mockStepUp.mockImplementation(async () => grant());
});

async function renderAccount(status: TwoFactorStatus, token: Record<string, unknown> = status.enabled ? ENROLLED : UNENROLLED) {
  mockStatus.mockResolvedValue(status);
  const view = renderWithProviders(<AccountPage />, { token });
  await screen.findByRole("heading", { name: t("twoFactorHeading") });
  return view;
}

async function enrol(enrolment = enrolmentFor()) {
  mockStart.mockResolvedValueOnce(enrolment);
  await press(t("setUpButton"));
  prove(false);
  await press(t("continueButton"), dialog());
  await screen.findByRole("heading", { name: t("enrolHeading") });
  return enrolment;
}

const typeCode = (code = freshCode()) => {
  fireEvent.change(screen.getByLabelText(new RegExp(t("codeLabel"))), { target: { value: code } });
  return code;
};

describe("the status drives the Security section", () => {
  it("offers Set up, and nothing else, while two-factor is off", async () => {
    await renderAccount(OFF);

    expect(screen.getByText(t("twoFactorOff"))).toBeInTheDocument();
    expect(screen.getByRole("button", { name: t("setUpButton") })).toBeInTheDocument();
    for (const name of [t("regenerateButton"), t("replaceButton"), t("turnOffButton")])
      expect(screen.queryByRole("button", { name })).not.toBeInTheDocument();
  });

  it("shows the codes left with Regenerate, Replace and Turn off once it is on", async () => {
    await renderAccount(ON);

    expect(screen.getByText(t("twoFactorOn"))).toBeInTheDocument();
    expect(screen.getByText(t("recoveryCodesLeft", { count: 10 }))).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: t("setUpButton") })).not.toBeInTheDocument();
    for (const name of [t("regenerateButton"), t("replaceButton"), t("turnOffButton")])
      expect(screen.getByRole("button", { name })).toBeInTheDocument();
  });

  it.each([[3, false], [2, true], [1, true], [0, true]])("with %i codes left, warns: %s", async (left, warns) => {
    await renderAccount({ enabled: true, recoveryCodesLeft: left });

    expect(screen.queryByText(t("recoveryCodesLow", { count: left })) !== null).toBe(warns);
    expect(screen.getByRole("button", { name: t("regenerateButton") })).toBeInTheDocument();
  });

  it("says when the status could not load, and loads it again on Try again", async () => {
    mockStatus.mockRejectedValueOnce(new Error("offline")).mockResolvedValue(ON);
    renderWithProviders(<AccountPage />, { token: ENROLLED });

    await screen.findByText(t("statusLoadFailed"));
    await press(t("statusRetry"));

    expect(await screen.findByText(t("twoFactorOn"))).toBeInTheDocument();
  });
});

describe("enrolment", () => {
  it("steps up with the password, shows the QR code, the key and the phone link, and warns before confirm", async () => {
    await renderAccount(OFF);
    const grantToken = crypto.randomUUID();
    mockStepUp.mockResolvedValueOnce({ token: grantToken, expiresAt: "2099-01-01T00:00:00Z" });
    mockStart.mockResolvedValueOnce(enrolmentFor());

    await press(t("setUpButton"));
    expect(within(dialog()).getByRole("heading", { name: i18n.t("users:confirmItsYouHeading") })).toBeInTheDocument();
    expect(within(dialog()).getByText(t("proofHintPassword"))).toBeInTheDocument();
    expect(within(dialog()).getByText(i18n.t("users:signedInAsCaption", { email: EMAIL }))).toBeInTheDocument();
    expect(codeField()).toBeNull();
    const proof = prove(false);
    await press(t("continueButton"), dialog());

    expect(mockStepUp).toHaveBeenCalledWith(proof);
    expect(mockStart).toHaveBeenCalledWith(grantToken);
    const { key: shownKey, otpauthUri: shownUri } = await mockStart.mock.results[0]!.value;
    const qr = screen.getByRole("img", { name: t("qrLabel") });
    expect(qr.querySelector("path")!.getAttribute("d")).toMatch(/^M\d+ \d+h1v1h-1z/);
    expect(screen.getByText(shownKey.match(/.{4}/g)!.join(" "))).toBeInTheDocument();
    expect(screen.getByRole("link", { name: t("openInAppLink") })).toHaveAttribute("href", shownUri);
    expect(screen.getByText(t("signOutWarning"))).toBeInTheDocument();
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });

  it("confirms the typed code, then shows the recovery codes once and never asks for them again", async () => {
    await renderAccount(OFF);
    await enrol();
    const codes = freshCodes();
    mockConfirm.mockResolvedValueOnce({ recoveryCodes: codes });
    mockStatus.mockResolvedValue({ enabled: true, recoveryCodesLeft: 10 });

    const code = typeCode();
    await press(t("confirmButton"));

    expect(mockConfirm).toHaveBeenCalledWith(code);
    const list = await screen.findByRole("list", { name: t("codesListLabel") });
    expect(within(list).getAllByRole("listitem").map((li) => li.textContent)).toEqual(codes);
    expect(screen.queryByText(t("codesReplaced"))).not.toBeInTheDocument();

    const done = screen.getByRole("button", { name: t("doneButton") });
    expect(done).toBeDisabled();
    fireEvent.click(screen.getByRole("checkbox", { name: t("savedCheckbox") }));
    await press(t("doneButton"));

    expect(await screen.findByText(t("twoFactorOn"))).toBeInTheDocument();
    for (const c of codes) expect(screen.queryByText(c)).not.toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole("button", { name: t("securityHeading") })).toHaveFocus());
    expect(mockConfirm).toHaveBeenCalledTimes(1);
    expect(mockRegenerate).not.toHaveBeenCalled();
  });

  // Maintainer, 2026-11-05: "Codes in a locked dialog". A modal dialog blocks
  // the sidebar, the bottom nav and Help, and nothing dismisses it before the
  // codes are saved.
  it("shows the codes in a dialog nothing dismisses until they are saved", async () => {
    await renderAccount(OFF);
    await enrol();
    mockConfirm.mockResolvedValueOnce({ recoveryCodes: freshCodes() });
    typeCode();
    await press(t("confirmButton"));

    const codes = await screen.findByRole("dialog", { name: t("codesHeading") });
    expect(within(codes).getByRole("button", { name: i18n.t("common:close") })).toBeDisabled();
    // Why it does not close, without reading out the codes.
    expect(codes).toHaveAccessibleDescription(t("codesLockedHint"));
    fireEvent.keyDown(codes, { key: "Escape" });
    expect(screen.getByRole("dialog", { name: t("codesHeading") })).toBeInTheDocument();

    fireEvent.click(within(codes).getByRole("checkbox", { name: t("savedCheckbox") }));
    expect(within(codes).getByRole("button", { name: i18n.t("common:close") })).toBeEnabled();
    fireEvent.keyDown(codes, { key: "Escape" });
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    expect(screen.queryByRole("list", { name: t("codesListLabel") })).not.toBeInTheDocument();
  });

  it.each([
    ["with attemptsLeft", { attemptsLeft: 3 }, () => t("codeIncorrectAttempts", { count: 3 })],
    ["malformed, with no attemptsLeft", {}, () => t("codeIncorrect")],
  ])("reports a wrong code %s, clears the field and stays on the QR code", async (_, extensions, text) => {
    await renderAccount(OFF);
    await enrol();
    mockConfirm.mockRejectedValueOnce(new ApiError(400, "Auth.EnrolmentCodeIncorrect", "Wrong.", { extensions }));

    typeCode();
    await press(t("confirmButton"));

    expect(await screen.findByRole("alert")).toHaveTextContent(text());
    expect(screen.getByLabelText(new RegExp(t("codeLabel")))).toHaveValue("");
    expect(screen.getByRole("img", { name: t("qrLabel") })).toBeInTheDocument();
  });

  it.each([
    ["an expired enrolment", new ApiError(409, "Auth.EnrolmentExpired", "Expired."), () => t("setUpExpired")],
    ["the fifth wrong code", new ApiError(400, "Auth.EnrolmentCodeIncorrect", "Wrong.", { extensions: { attemptsLeft: 0 } }), () => t("setUpTooManyAttempts")],
    // The pending enrolment is bound to the old epoch, so Try again could never work.
    ["a stale session", new ApiError(401, "Auth.CredentialsSuperseded", "Your credentials changed."), () => t("setUpSessionChanged")],
  ])("after %s, drops the QR code and starts again from a fresh step-up", async (_, err, text) => {
    await renderAccount(OFF);
    const { key } = await enrol();
    mockConfirm.mockRejectedValueOnce(err);

    typeCode();
    await press(t("confirmButton"));

    expect(await screen.findByText(text())).toBeInTheDocument();
    expect(screen.queryByRole("img", { name: t("qrLabel") })).not.toBeInTheDocument();
    expect(screen.queryByText(key.match(/.{4}/g)!.join(" "))).not.toBeInTheDocument();

    mockStart.mockResolvedValueOnce(enrolmentFor());
    await press(t("startAgainButton"));
    prove(false);
    await press(t("continueButton"), dialog());
    expect(await screen.findByRole("img", { name: t("qrLabel") })).toBeInTheDocument();
    expect(mockStart).toHaveBeenCalledTimes(2);
  });
});

describe("an enrolled user's step-up actions", () => {
  it("replaces the authenticator app with the password and a code, and confirm returns no codes", async () => {
    await renderAccount(ON);
    mockStart.mockResolvedValueOnce(enrolmentFor());

    await press(t("replaceButton"));
    expect(within(dialog()).getByRole("heading", { name: t("replaceTitle") })).toBeInTheDocument();
    expect(within(dialog()).getByText(t("proofHint"))).toBeInTheDocument();
    const proof = prove(true);
    await press(t("continueButton"), dialog());

    expect(mockStepUp).toHaveBeenCalledWith(proof);
    expect(await screen.findByRole("heading", { name: t("replaceHeading") })).toBeInTheDocument();
    mockConfirm.mockResolvedValueOnce({ recoveryCodes: undefined });
    typeCode();
    await press(t("confirmButton"));

    expect(await screen.findByText(t("replacedMessage"))).toBeInTheDocument();
    expect(screen.queryByRole("list", { name: t("codesListLabel") })).not.toBeInTheDocument();
  });

  it("regenerates the recovery codes and shows the new set as replacing the old", async () => {
    await renderAccount({ enabled: true, recoveryCodesLeft: 1 });
    const codes = freshCodes();
    mockRegenerate.mockResolvedValueOnce({ recoveryCodes: codes });

    await press(t("regenerateButton"));
    const proof = prove(true);
    await press(t("regenerateButton"), dialog());

    expect(mockStepUp).toHaveBeenCalledWith(proof);
    expect(await screen.findByText(t("codesReplaced"))).toBeInTheDocument();
    expect(within(screen.getByRole("list", { name: t("codesListLabel") })).getAllByRole("listitem")).toHaveLength(10);
  });

  // #1277 contract item 8: the status was stale.
  it("says Regenerate found two-factor off, and loads the status again", async () => {
    await renderAccount(ON);
    mockRegenerate.mockRejectedValueOnce(new ApiError(409, "Auth.TwoFactorNotEnabled", "Not enabled."));
    mockStatus.mockResolvedValue(OFF);

    await press(t("regenerateButton"));
    prove(true);
    await press(t("regenerateButton"), dialog());

    expect(await within(dialog()).findByRole("alert")).toHaveTextContent(t("regenerateNotEnabled"));
    expect(mockStatus).toHaveBeenCalledTimes(2);
  });

  it("turns two-factor off and reports it", async () => {
    await renderAccount(ON);
    const grantToken = crypto.randomUUID();
    mockStepUp.mockResolvedValueOnce({ token: grantToken, expiresAt: "2099-01-01T00:00:00Z" });
    mockTurnOff.mockResolvedValueOnce(undefined);
    mockStatus.mockResolvedValue(OFF);

    await press(t("turnOffButton"));
    expect(within(dialog()).getByText(t("turnOffBody"))).toBeInTheDocument();
    prove(true);
    await press(t("turnOffButton"), dialog());

    expect(mockTurnOff).toHaveBeenCalledWith(grantToken);
    expect(await screen.findByText(t("turnedOffMessage"))).toBeInTheDocument();
    expect(await screen.findByText(t("twoFactorOff"))).toBeInTheDocument();
  });

  it("reports a stale session's turn-off once, as nothing changed", async () => {
    await renderAccount(ON);
    mockTurnOff.mockRejectedValueOnce(new ApiError(401, "Auth.CredentialsSuperseded", "Your credentials changed."));

    await press(t("turnOffButton"));
    prove(true);
    await press(t("turnOffButton"), dialog());

    expect(await within(dialog()).findByRole("alert")).toHaveTextContent(t("sessionChanged"));
    expect(mockTurnOff).toHaveBeenCalledTimes(1);
  });
});

// The useDialogSession rules (#1280): every path clears the proof, and a grant
// that lands after the dialog or the page is gone sends nothing.
describe("the step-up proof", () => {
  const actions = [
    ["Set up", OFF, () => t("setUpButton"), () => t("continueButton")],
    ["Replace", ON, () => t("replaceButton"), () => t("continueButton")],
    ["Regenerate", ON, () => t("regenerateButton"), () => t("regenerateButton")],
    ["Turn off", ON, () => t("turnOffButton"), () => t("turnOffButton")],
  ] as const;

  it.each(actions)("%s clears it on cancel, so the reopened dialog is empty", async (_, status, open) => {
    await renderAccount(status);
    await press(open());
    prove(status.enabled);

    await press(i18n.t("common:cancel"), dialog());
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    await press(open());

    expect(passwordField()).toHaveValue("");
    if (status.enabled) expect(codeField()).toHaveValue("");
  });

  it.each(actions)("%s clears it the moment the proof is sent, and after a refusal", async (_, status, open, submit) => {
    await renderAccount(status);
    mockStepUp.mockRejectedValueOnce(new ApiError(400, "Users.CurrentPasswordIncorrect", "Wrong password."));
    await press(open());
    prove(status.enabled);

    await press(submit(), dialog());

    expect(await within(dialog()).findByRole("alert")).toBeInTheDocument();
    expect(passwordField()).toHaveValue("");
    if (status.enabled) expect(codeField()).toHaveValue("");
  });

  it.each(actions)("%s sends nothing when its grant lands after the page is gone", async (_, status, open, submit) => {
    const view = await renderAccount(status);
    let release!: (value: ReturnType<typeof grant>) => void;
    mockStepUp.mockReturnValueOnce(new Promise((resolve) => { release = resolve; }));
    await press(open());
    prove(status.enabled);
    await press(submit(), dialog());

    view.unmount();
    await act(async () => { release(grant()); });

    expect(mockStart).not.toHaveBeenCalled();
    expect(mockRegenerate).not.toHaveBeenCalled();
    expect(mockTurnOff).not.toHaveBeenCalled();
  });

  it("drops the key and clears a typed proof the instant the session ends", async () => {
    mockStatus.mockResolvedValue(ON);
    const tree = (isAuthenticated: boolean) => (
      <MemoryRouter>
        <AuthContext.Provider value={{
          isAuthenticated, isLoading: false, isAdmin: false, role: "Worker", userId: "u1",
          mustChangePassword: false, twoFactor: true, unauthenticatedReason: null,
          login: vi.fn(), completeSecondFactor: vi.fn(), logout: vi.fn(),
        }}>
          <MeContext.Provider value={{ id: "u1", email: EMAIL, name: null, role: "Worker", language: null, preferredStepperUnit: null }}>
            <FarmContext.Provider value={farmState({ farm: null })}><AccountPage /></FarmContext.Provider>
          </MeContext.Provider>
        </AuthContext.Provider>
      </MemoryRouter>
    );
    const view = render(tree(true));
    await screen.findByRole("heading", { name: t("twoFactorHeading") });
    mockStart.mockResolvedValueOnce(enrolmentFor());
    await press(t("replaceButton"));
    prove(true);
    await press(t("continueButton"), dialog());
    const { key } = await mockStart.mock.results[0]!.value;
    await screen.findByRole("img", { name: t("qrLabel") });

    view.rerender(tree(false));

    expect(screen.queryByRole("img", { name: t("qrLabel") })).not.toBeInTheDocument();
    expect(screen.queryByText(key.match(/.{4}/g)!.join(" "))).not.toBeInTheDocument();
    view.rerender(tree(true));
    await press(t("turnOffButton"));
    prove(true);

    view.rerender(tree(false));

    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    view.rerender(tree(true));
    await press(t("turnOffButton"));
    expect(passwordField()).toHaveValue("");
    expect(codeField()).toHaveValue("");
  });
});
