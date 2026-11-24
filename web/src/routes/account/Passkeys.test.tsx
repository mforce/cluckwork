import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { act, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { AccountPage } from "../AccountPage";
import { renderWithProviders } from "../../test/renderWithProviders";
import { account } from "../../test/fixtures";
import { getTwoFactorStatus, listMyConnectedApps, listPasskeys, registerPasskey, removePasskey, renamePasskey, startPasskeyRegistration } from "../../api/cluckwork";
import type { Passkey, PasskeyList } from "../../api/cluckwork";
import { ApiError, stepUp } from "../../api/client";
import i18n from "../../i18n";
import { freshCode } from "../../test/codes";
import { freshPassword } from "../../test/passwords";

// #1284 — Account → Security's passkeys (record 1282, #1283 "Account /
// Security A" and "Registration A"). The network is mocked at the API seam and
// the browser's WebAuthn at window.PublicKeyCredential and
// navigator.credentials; no real authenticator runs.
vi.mock("../../api/cluckwork", async (importOriginal) => ({
  ...await importOriginal<typeof import("../../api/cluckwork")>(),
  getTwoFactorStatus: vi.fn(),
  listMyConnectedApps: vi.fn(),
  listPasskeys: vi.fn(),
  renamePasskey: vi.fn(),
  startPasskeyRegistration: vi.fn(),
  registerPasskey: vi.fn(),
  removePasskey: vi.fn(),
}));
vi.mock("../../api/client", async (importOriginal) => ({
  ...await importOriginal<typeof import("../../api/client")>(),
  stepUp: vi.fn(),
  logout: vi.fn(),
  setOnTokensChanged: vi.fn(),
  setOnUnauthenticated: vi.fn(),
}));

const mockList = vi.mocked(listPasskeys);
const mockStepUp = vi.mocked(stepUp);
const mockStart = vi.mocked(startPasskeyRegistration);
const mockRegister = vi.mocked(registerPasskey);
const mockRemove = vi.mocked(removePasskey);
const mockRename = vi.mocked(renamePasskey);
const create = vi.fn();
const parse = vi.fn((options: unknown) => ({ parsed: options }));

const t = (key: string, options: Record<string, unknown> = {}): string => i18n.t(`account:${key}` as "account:heading", options);
const UNENROLLED = { sub: "u1", role: "Worker" };
const ENROLLED = { ...UNENROLLED, two_factor: "true" };
const FARM = account({ name: "Sunrise Farm" });
const grant = () => ({ token: crypto.randomUUID(), expiresAt: "2099-01-01T00:00:00Z" });
const passkey = (over: Partial<Passkey> = {}): Passkey => ({
  id: crypto.randomUUID(), name: "Laptop", createdAt: "2026-11-12T10:00:00Z", synced: false, address: null, ...over,
});
const dialog = () => screen.getByRole("dialog");
const press = async (name: string | RegExp, scope: HTMLElement = document.body) => {
  await act(async () => { fireEvent.click(within(scope).getByRole("button", { name })); });
};
const section = () => screen.getByRole("region", { name: t("passkeysHeading") });
// What the browser hands back: an object whose toJSON() is the registration JSON.
const credential = () => {
  const json = { id: crypto.randomUUID(), rawId: crypto.randomUUID(), type: "public-key", response: {} };
  return { json, value: { toJSON: () => json } };
};

beforeEach(() => {
  vi.resetAllMocks();
  vi.mocked(getTwoFactorStatus).mockResolvedValue({ enabled: false, recoveryCodesLeft: 0 });
  vi.mocked(listMyConnectedApps).mockResolvedValue([]);
  mockStepUp.mockImplementation(async () => grant());
  parse.mockImplementation((options: unknown) => ({ parsed: options }));
  vi.stubGlobal("PublicKeyCredential", { parseCreationOptionsFromJSON: parse });
  Object.defineProperty(navigator, "credentials", { value: { create }, configurable: true });
});
afterEach(() => {
  vi.unstubAllGlobals();
});

async function renderAccount(list: PasskeyList, token: Record<string, unknown> = UNENROLLED) {
  mockList.mockResolvedValue(list);
  renderWithProviders(<AccountPage />, { token, farm: FARM });
  await screen.findByRole("heading", { name: t("twoFactorHeading") });
  await waitFor(() => expect(mockList).toHaveBeenCalled());
}

// Add, then the step-up with the password, landing on the create step.
async function toCreateStep() {
  await press(t("addPasskeyButton"), section());
  fireEvent.change(within(dialog()).getByLabelText(/Your current password/), { target: { value: freshPassword() } });
  await press(t("continueButton"), dialog());
  await screen.findByRole("heading", { name: t("createPasskeyHeading") });
}

describe("the list", () => {
  it("shows each passkey's name, creation date and synced state, and no address with one origin", async () => {
    const synced = passkey({ name: "Phone", synced: true });
    await renderAccount({ enabled: true, passkeys: [synced, passkey()] });

    const rows = within(section()).getAllByRole("listitem");
    expect(rows).toHaveLength(2);
    expect(within(rows[0]!).getByRole("heading", { name: "Phone" })).toBeInTheDocument();
    expect(rows[0]!.querySelector("time")).toHaveAttribute("dateTime", "2026-11-12");
    expect(rows[0]).toHaveTextContent(t("passkeySynced"));
    expect(rows[1]).not.toHaveTextContent(t("passkeySynced"));
    expect(within(section()).getByText(t("passkeysCount", { count: 2, max: 5 }))).toBeInTheDocument();
    expect(within(section()).queryByText(t("passkeysMultiAddress"))).not.toBeInTheDocument();
  });

  it("shows each passkey's address under its name when the server sends it", async () => {
    await renderAccount({ enabled: true, passkeys: [passkey({ address: "https://farm.example.test" }), passkey({ address: "https://office.example.test" })] });

    const rows = within(section()).getAllByRole("listitem");
    expect(rows[0]).toHaveTextContent("https://farm.example.test");
    expect(rows[1]).toHaveTextContent("https://office.example.test");
    expect(within(section()).getByText(t("passkeysMultiAddress"))).toBeInTheDocument();
  });

  it("offers Add, Rename and Remove while passkeys are on", async () => {
    await renderAccount({ enabled: true, passkeys: [passkey()] });

    expect(within(section()).getByRole("button", { name: t("addPasskeyButton") })).toBeEnabled();
    expect(within(section()).getByRole("button", { name: t("renameButton") })).toBeInTheDocument();
    expect(within(section()).getByRole("button", { name: t("removeButton") })).toBeInTheDocument();
  });

  it("keeps Remove only while passkeys are off at this address", async () => {
    await renderAccount({ enabled: false, passkeys: [passkey()] });

    expect(within(section()).getByRole("button", { name: t("removeButton") })).toBeInTheDocument();
    expect(within(section()).queryByRole("button", { name: t("addPasskeyButton") })).not.toBeInTheDocument();
    expect(within(section()).queryByRole("button", { name: t("renameButton") })).not.toBeInTheDocument();
    expect(within(section()).getByText(t("passkeysOffHint"))).toBeInTheDocument();
  });

  it("shows no passkey section while passkeys are off and there are none", async () => {
    await renderAccount({ enabled: false, passkeys: [] });

    expect(screen.queryByRole("region", { name: t("passkeysHeading") })).not.toBeInTheDocument();
  });

  it("says there are none, and offers Add, while passkeys are on", async () => {
    await renderAccount({ enabled: true, passkeys: [] });

    expect(within(section()).getByText(t("passkeysEmpty"))).toBeInTheDocument();
    expect(within(section()).getByRole("button", { name: t("addPasskeyButton") })).toBeEnabled();
  });

  it("disables Add at five passkeys and says why", async () => {
    await renderAccount({ enabled: true, passkeys: Array.from({ length: 5 }, () => passkey()) });

    expect(within(section()).getByRole("button", { name: t("addPasskeyButton") })).toBeDisabled();
    expect(within(section()).getByText(t("passkeysLimit", { max: 5 }))).toBeInTheDocument();
  });
});

describe("adding a passkey", () => {
  it("steps up, then creates the passkey from the options and sends the credential, the name and the grant", async () => {
    await renderAccount({ enabled: true, passkeys: [] });
    const proof = grant();
    mockStepUp.mockResolvedValueOnce(proof);
    const ceremony = crypto.randomUUID();
    const options = { challenge: crypto.randomUUID() };
    mockStart.mockResolvedValueOnce({ ceremony, options: options as unknown as PublicKeyCredentialCreationOptionsJSON });
    const made = credential();
    create.mockResolvedValueOnce(made.value);
    mockRegister.mockResolvedValueOnce(passkey({ name: "Barn tablet" }));

    await toCreateStep();
    expect(mockStart).not.toHaveBeenCalled();
    fireEvent.change(screen.getByLabelText(new RegExp(t("passkeyNameLabel"))), { target: { value: "  Barn tablet " } });
    await press(t("createPasskeyButton"));

    expect(parse).toHaveBeenCalledWith(options);
    expect(create).toHaveBeenCalledWith({ publicKey: { parsed: options } });
    expect(mockRegister).toHaveBeenCalledWith({ ceremony, credential: made.json, name: "Barn tablet" }, proof.token);
    expect(await screen.findByRole("heading", { name: t("passkeyCreatedHeading") })).toBeInTheDocument();
    expect(screen.getByText(t("passkeyReady", { name: "Barn tablet" }))).toBeInTheDocument();
  });

  // Maintainer, 2026-11-24: "One neutral name (Recommended)", whatever the device.
  it.each([
    ["a computer", "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36"],
    ["a phone", "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148"],
  ])("proposes the one neutral name on %s and says how the device will label the passkey", async (_, agent) => {
    vi.spyOn(navigator, "userAgent", "get").mockReturnValue(agent);
    await renderAccount({ enabled: true, passkeys: [] });

    await toCreateStep();

    expect(screen.getByLabelText(new RegExp(t("passkeyNameLabel")))).toHaveValue(t("passkeyDefaultName"));
    expect(screen.getByText(t("passkeyDeviceName", { email: "test@farm.local", farm: "Sunrise Farm" }))).toBeInTheDocument();
  });

  it.each([
    ["a first passkey with no app", { enabled: true, passkeys: [] }, UNENROLLED, true],
    ["a later passkey", { enabled: true, passkeys: [passkey()] }, UNENROLLED, false],
    ["a first passkey beside the app", { enabled: true, passkeys: [] }, ENROLLED, false],
  ] as const)("warns that other sessions end only for %s", async (_, list, token, warns) => {
    if (token === ENROLLED) vi.mocked(getTwoFactorStatus).mockResolvedValue({ enabled: true, recoveryCodesLeft: 10 });
    await renderAccount({ ...list, passkeys: [...list.passkeys] }, token);

    await press(t("addPasskeyButton"), section());
    fireEvent.change(within(dialog()).getByLabelText(/Your current password/), { target: { value: freshPassword() } });
    if (token === ENROLLED)
      fireEvent.change(within(dialog()).getByLabelText(/Authenticator or recovery code/), { target: { value: freshCode() } });
    await press(t("continueButton"), dialog());
    await screen.findByRole("heading", { name: t("createPasskeyHeading") });

    expect(screen.queryByText(t("passkeySessionWarning")) !== null).toBe(warns);
  });

  it("keeps the grant after a browser cancel and offers Try again", async () => {
    await renderAccount({ enabled: true, passkeys: [] });
    const proof = grant();
    mockStepUp.mockResolvedValueOnce(proof);
    mockStart.mockImplementation(async () => ({ ceremony: crypto.randomUUID(), options: {} as PublicKeyCredentialCreationOptionsJSON }));
    create.mockRejectedValueOnce(new DOMException("The operation either timed out or was not allowed.", "NotAllowedError"));

    await toCreateStep();
    await press(t("createPasskeyButton"));

    expect(await screen.findByText(t("passkeyCancelled"))).toBeInTheDocument();
    expect(mockRegister).not.toHaveBeenCalled();

    create.mockResolvedValueOnce(credential().value);
    mockRegister.mockResolvedValueOnce(passkey());
    await press(t("tryAgainButton"));

    expect(mockStepUp).toHaveBeenCalledTimes(1);
    expect(mockRegister).toHaveBeenCalledWith(expect.anything(), proof.token);
  });

  it("asks for a new step-up after a refused finish, which spent the grant", async () => {
    await renderAccount({ enabled: true, passkeys: [] });
    mockStart.mockResolvedValue({ ceremony: crypto.randomUUID(), options: {} as PublicKeyCredentialCreationOptionsJSON });
    create.mockResolvedValue(credential().value);
    mockRegister.mockRejectedValueOnce(new ApiError(400, "Auth.PasskeyRefused", i18n.t("errors:Auth.PasskeyRefused")));

    await toCreateStep();
    await press(t("createPasskeyButton"));

    expect(await screen.findByText(i18n.t("errors:Auth.PasskeyRefused"))).toBeInTheDocument();
    await press(t("startAgainButton"));

    expect(dialog()).toHaveTextContent(t("addPasskeyBody"));
    expect(mockRegister).toHaveBeenCalledTimes(1);
  });

  it("says when this device already holds a passkey for the account", async () => {
    await renderAccount({ enabled: true, passkeys: [passkey()] });
    mockStart.mockResolvedValue({ ceremony: crypto.randomUUID(), options: {} as PublicKeyCredentialCreationOptionsJSON });
    create.mockRejectedValueOnce(new DOMException("excluded", "InvalidStateError"));

    await toCreateStep();
    await press(t("createPasskeyButton"));

    expect(await screen.findByText(t("passkeyExists"))).toBeInTheDocument();
    expect(mockRegister).not.toHaveBeenCalled();
  });

  it("shows the unsupported screen, with no step-up, where the browser has no WebAuthn JSON helpers", async () => {
    vi.stubGlobal("PublicKeyCredential", undefined);
    await renderAccount({ enabled: true, passkeys: [] });

    await press(t("addPasskeyButton"), section());

    expect(await screen.findByRole("heading", { name: t("passkeyUnsupportedHeading") })).toBeInTheDocument();
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(mockStepUp).not.toHaveBeenCalled();
  });

  it("returns to the list on Done and reads it again", async () => {
    await renderAccount({ enabled: true, passkeys: [] });
    mockStart.mockResolvedValue({ ceremony: crypto.randomUUID(), options: {} as PublicKeyCredentialCreationOptionsJSON });
    create.mockResolvedValue(credential().value);
    const added = passkey({ name: "Phone" });
    mockRegister.mockResolvedValueOnce(added);
    mockList.mockResolvedValue({ enabled: true, passkeys: [added] });

    await toCreateStep();
    await press(t("createPasskeyButton"));
    await press(t("doneButton"));

    expect(await within(section()).findByRole("heading", { name: "Phone" })).toBeInTheDocument();
  });
});

describe("names", () => {
  it("refuses a create step name of only spaces, with no browser call and the grant kept", async () => {
    await renderAccount({ enabled: true, passkeys: [] });
    mockStart.mockResolvedValue({ ceremony: crypto.randomUUID(), options: {} as PublicKeyCredentialCreationOptionsJSON });
    create.mockResolvedValue(credential().value);

    await toCreateStep();
    fireEvent.change(screen.getByLabelText(new RegExp(t("passkeyNameLabel"))), { target: { value: "   " } });
    expect(screen.getByRole("button", { name: t("createPasskeyButton") })).toBeDisabled();
    fireEvent.submit(screen.getByRole("button", { name: t("createPasskeyButton") }).closest("form")!);
    await act(async () => {});

    expect(mockStart).not.toHaveBeenCalled();
    expect(create).not.toHaveBeenCalled();
    expect(screen.getByText(t("passkeyNameRequired"))).toBeInTheDocument();
  });

  it("refuses a new name of only spaces", async () => {
    await renderAccount({ enabled: true, passkeys: [passkey()] });

    await press(t("renameButton"), section());
    fireEvent.change(within(dialog()).getByLabelText(new RegExp(t("passkeyNameLabel"))), { target: { value: "   " } });
    expect(within(dialog()).getByRole("button", { name: t("saveNameButton") })).toBeDisabled();
    fireEvent.submit(dialog().querySelector("form")!);
    await act(async () => {});

    expect(mockRename).not.toHaveBeenCalled();
    expect(within(dialog()).getByText(t("passkeyNameRequired"))).toBeInTheDocument();
  });

  it("keeps the typed name when Start again steps up after a refused finish", async () => {
    await renderAccount({ enabled: true, passkeys: [] });
    mockStart.mockResolvedValue({ ceremony: crypto.randomUUID(), options: {} as PublicKeyCredentialCreationOptionsJSON });
    create.mockResolvedValue(credential().value);
    mockRegister.mockRejectedValueOnce(new ApiError(400, "Auth.PasskeyRefused", i18n.t("errors:Auth.PasskeyRefused")));

    await toCreateStep();
    fireEvent.change(screen.getByLabelText(new RegExp(t("passkeyNameLabel"))), { target: { value: "Barn tablet" } });
    await press(t("createPasskeyButton"));
    await press(t("startAgainButton"));
    fireEvent.change(within(dialog()).getByLabelText(/Your current password/), { target: { value: freshPassword() } });
    await press(t("continueButton"), dialog());

    expect(screen.getByLabelText(new RegExp(t("passkeyNameLabel")))).toHaveValue("Barn tablet");
  });
});

describe("browser errors", () => {
  it.each([
    ["a DOMException other than the two it words", new DOMException("blocked", "SecurityError")],
    ["an error that is not a DOMException", new TypeError("options are malformed")],
  ])("says the passkey could not be created for %s", async (_, err) => {
    await renderAccount({ enabled: true, passkeys: [] });
    mockStart.mockResolvedValue({ ceremony: crypto.randomUUID(), options: {} as PublicKeyCredentialCreationOptionsJSON });
    create.mockRejectedValueOnce(err);

    await toCreateStep();
    await press(t("createPasskeyButton"));

    expect(await screen.findByText(t("passkeyFailed"))).toBeInTheDocument();
    expect(screen.queryByText(err.message)).not.toBeInTheDocument();
    expect(mockRegister).not.toHaveBeenCalled();
  });
});

describe("renaming and removing", () => {
  it("renames with the trimmed name and no step-up", async () => {
    const key = passkey();
    await renderAccount({ enabled: true, passkeys: [key] });
    mockRename.mockResolvedValueOnce(undefined);

    await press(t("renameButton"), section());
    fireEvent.change(within(dialog()).getByLabelText(new RegExp(t("passkeyNameLabel"))), { target: { value: " Office laptop  " } });
    await press(t("saveNameButton"), dialog());

    expect(mockRename).toHaveBeenCalledWith(key.id, "Office laptop");
    expect(mockStepUp).not.toHaveBeenCalled();
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
  });

  it("removes with a step-up, warns about other sessions, and says it is done", async () => {
    const key = passkey();
    await renderAccount({ enabled: true, passkeys: [key] });
    const proof = grant();
    mockStepUp.mockResolvedValueOnce(proof);
    mockRemove.mockResolvedValueOnce(undefined);

    await press(t("removeButton"), section());
    expect(within(dialog()).getByText(t("passkeySessionWarning"))).toBeInTheDocument();
    const password = freshPassword();
    fireEvent.change(within(dialog()).getByLabelText(/Your current password/), { target: { value: password } });
    await press(t("removePasskeyButton"), dialog());

    expect(mockStepUp).toHaveBeenCalledWith({ password });
    expect(mockRemove).toHaveBeenCalledWith(key.id, proof.token);
    expect(await screen.findByText(t("passkeyRemovedMessage"))).toBeInTheDocument();
  });

  it("removes while passkeys are off", async () => {
    const key = passkey();
    await renderAccount({ enabled: false, passkeys: [key] });
    mockRemove.mockResolvedValueOnce(undefined);

    await press(t("removeButton"), section());
    fireEvent.change(within(dialog()).getByLabelText(/Your current password/), { target: { value: freshPassword() } });
    await press(t("removePasskeyButton"), dialog());

    expect(mockRemove).toHaveBeenCalledWith(key.id, expect.any(String));
  });

  it("says the sign-in changed when the removal was refused as superseded", async () => {
    await renderAccount({ enabled: true, passkeys: [passkey()] });
    mockRemove.mockRejectedValueOnce(new ApiError(401, "Auth.CredentialsSuperseded", "superseded"));

    await press(t("removeButton"), section());
    fireEvent.change(within(dialog()).getByLabelText(/Your current password/), { target: { value: freshPassword() } });
    await press(t("removePasskeyButton"), dialog());

    expect(await within(dialog()).findByText(t("sessionChanged"))).toBeInTheDocument();
  });
});
