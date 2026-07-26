// The English catalog — the SOURCE OF TRUTH and the fallback (#182). Namespaced
// by area. Keys are camelCase; values are sentence-case UI copy. Adding a key
// here extends the compile-time key type (see types/i18next.d.ts), so a t("typo")
// is a build error.
export const en = {
  common: {
    cancel: "Cancel",
    save: "Save",
    retry: "retry",
  },
  auth: {
    title: "Cluckwork",
    email: "Email",
    password: "Password",
    signIn: "Sign in",
    signingIn: "Signing in…",
    invalidCredentials: "Invalid email or password.",
    tooManyAttempts:
      "Too many sign-in attempts. Please wait a few minutes and try again.",
    apiDown: "Could not sign in. Is the API running?",
  },
  account: {
    preferences: "Preferences",
    language: "Language",
    languageHint: "The language the interface is shown in, just for you.",
  },
  // Keyed by the API's stable validation codes (#45), which contain dots
  // (e.g. "Me.Language.Format"). With keySeparator:false (see init) these are
  // literal flat keys, not nested paths. Filled in Task 4.
  errors: {},
  sales: {
    // Filled in Task 7 (the Sales pilot).
  },
} as const;

export type Resources = typeof en;
