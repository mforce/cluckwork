import { en } from "./en";

// Every fully-qualified i18next key that resolves inside the `enums` namespace,
// e.g. "enums:status.Active". Constrains the KEYS maps in the enums-* modules
// so a value can only be a key that en.enums actually defines.
export type EnumsKey = `enums:${Extract<keyof typeof en.enums, string>}`;
