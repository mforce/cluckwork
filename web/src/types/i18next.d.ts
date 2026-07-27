// Compile-time key checking (#182): t("auth:signIn") is typed; t("auth:typo") is
// a build error. The accepted burden is keeping this in sync with en.ts — which
// is automatic, since `resources` IS `typeof en`.
import "i18next";
import type { Resources } from "../i18n/en";

declare module "i18next" {
  interface CustomTypeOptions {
    defaultNS: "common";
    resources: Resources;
  }
}
