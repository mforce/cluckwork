import { describe, expect, it } from "vitest";
import { ApiError } from "../api/errors";
import i18n from "../i18n";
import { secondFactorMessage } from "./secondFactorMessage";

// #1279 — every answer to a request that carried an authenticator code is
// marked, but only a wrong code gets the clock hint.
describe("secondFactorMessage with an authenticator code sent", () => {
  const marked = (status: number, title: string) =>
    secondFactorMessage(new ApiError(status, title, "server text", { sentAuthenticatorCode: true }));

  it("adds the clock hint to a wrong code", () => {
    expect(marked(400, "Auth.SecondFactorIncorrect"))
      .toBe(`${i18n.t("auth:secondFactorIncorrect")} ${i18n.t("auth:secondFactorClockHint")}`);
  });

  it.each([
    [400, "Auth.SecondFactorRequired", "auth:secondFactorRequired"],
    [400, "Auth.SecondFactorKeyUnavailable", "auth:secondFactorKeyUnavailable"],
    [429, "Auth.SecondFactorThrottled", "auth:secondFactorThrottled"],
    [503, "Auth.StepUpRetry", "auth:stepUpRetry"],
  ] as const)("answers %i %s with its own text and no hint", (status, title, key) => {
    expect(marked(status, title)).toBe(i18n.t(key));
  });

  it("leaves a wrong password to its surface", () => {
    expect(marked(400, "Users.CurrentPasswordIncorrect")).toBeNull();
  });
});
