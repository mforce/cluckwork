import { apiDelete, apiGet, apiGetBlob, apiPut, apiPutBytes } from "../client";

// --- Account ---

// The full §4.5 localization set. currencyCode/currencyMinorUnit keep their
// names and meaning from before #123 — every money field parses with them.
export interface Account {
  id: string;
  name: string;
  currencyCode: string;
  currencyMinorUnit: number;
  currencySymbol: string;
  timeZoneId: string;
  locale: string;
  unitSystem: string;
  // Null means "follow the locale" rather than a day.
  firstDayOfWeek: string | null;
  dateFormatOverride: string | null;
  timeFormatOverride: string | null;
  // Optimistic-concurrency token — sent back on save; a mismatch is a 409.
  version: number;
  // Null when the farm has no logo: the chrome shows app branding and never
  // calls /account/logo. Otherwise it changes whenever the logo is replaced,
  // which is what makes the cached blob URL self-invalidating.
  logoContentHash: string | null;
  // Same contract as logoContentHash, for the post-login splash banner (#179):
  // null means no banner is set, so the splash is skipped entirely.
  bannerContentHash: string | null;
  // The farm's accent palette (#149) — farm-wide and Owner-chosen, unlike the
  // light/night toggle, which stays a per-user device preference. The API is
  // the source of truth; localStorage only caches it for the pre-paint script.
  brand: string;
  // #444 — the farm-default Daily Entry stepper pack unit (an EggUnitConversion
  // code, e.g. "Tray"). A user's own Me.preferredStepperUnit overrides this.
  defaultStepperUnit: string;
  // #612 — role-agnostic on purpose: true only for a restricted plain Worker
  // under AllFarmFlocks, so the Sales screen can show the persistent generic
  // notice without exposing the raw policy (that lives only on
  // FarmSettings, admin-only) to every role.
  showFarmWideSaleAllocationNotice: boolean;
  // #727 — the largest discount THIS caller may put on a sale line, as a
  // percent, or null. Same standing as showFarmWideSaleAllocationNotice above:
  // derived per caller, a display hint, never the authority.
  //
  // Null covers BOTH "the farm sets no ceiling" and "you may exceed it"
  // (Owner/Manager), because both mean the same thing to the screen. So
  // non-null reads simply as "this caller is bound by this number", and 0 is a
  // real ceiling rather than an absent one.
  //
  // Claims-derived, so a user promoted mid-session keeps the stale hint until
  // their token refreshes. The failure mode is a stale warning, never a wrong
  // outcome: the confirm handler's fresh in-transaction role read decides.
  yourMaxDiscountPercent: number | null;
  // #1146 — the farm's switch; off refuses consent and every app's token.
  allowConnectedApps: boolean;
}

// Clients need the account currency to parse money input correctly — a JPY
// amount has 0 decimals, so assuming 2 silently multiplies costs by 100 — and
// the timezone to cap date inputs at the FARM's today rather than the
// browser's (#123). Readable by every authenticated role.
export const getAccount = () => apiGet<Account>("/account");

// The signed-in user's own profile (#45 GET /me). Distinct from the admin `User`
// list DTO: this carries the user's UI-language preference. Role is echoed for
// convenience; the JWT stays authoritative for gating.
export interface Me {
  id: string;
  email: string;
  name: string | null;
  role: string;
  language: string | null;
  // #444 — overrides Account.defaultStepperUnit for this user's Daily Entry
  // steppers. Null = follow the farm default.
  preferredStepperUnit: string | null;
}

export const getMe = () => apiGet<Me>("/me");

// PUT one absolute preference (#45). null clears it. apiPut mints a fresh
// Idempotency-Key per call. Returns void (204).
export const putMeLanguage = (language: string | null): Promise<void> =>
  apiPut<void>("/me/language", { language });

// #444 — same shape as putMeLanguage: one absolute preference, null clears it.
export const putMeStepperUnit = (unit: string | null): Promise<void> =>
  apiPut<void>("/me/stepper-unit", { unit });

export interface FarmSettings {
  settings: Account;
  // False once any sales order, payment or expense has recorded an amount:
  // §4.6 locks the currency for good. Surfaced so the screen can disable the
  // field with a reason instead of letting the user find out as a 422.
  canChangeCurrency: boolean;
  // The logo upload cap in bytes (#123). It is server CONFIG, so it rides here
  // rather than being duplicated as a client constant — a hardcoded copy would
  // silently diverge from the server the moment the config changed.
  logoMaxUploadBytes: number;
  // Same contract, for the banner (#179) — a separate, larger cap.
  bannerMaxUploadBytes: number;
  // #612/#729 — the raw policy, Owner-only (like canChangeCurrency above). Every
  // other role only ever sees the derived Account.showFarmWideSaleAllocationNotice.
  workerSaleAllocationPolicy: string;
  // #727/#729 — the farm's own ceiling, Owner-only on the same terms. Every other
  // role only ever sees the per-caller Account.yourMaxDiscountPercent, which is
  // null for the Owners and Managers who may exceed it; only Owners edit it.
  maxDiscountPercent: number | null;
}

export interface UpdateFarmSettings {
  name: string;
  timeZoneId: string;
  locale: string;
  currencyCode: string;
  unitSystem: string;
  firstDayOfWeek: string | null;
  dateFormatOverride: string | null;
  timeFormatOverride: string | null;
  brand: string;
  defaultStepperUnit: string;
  workerSaleAllocationPolicy: string;
  // #727 — null clears the ceiling; 0 is a legal, different setting.
  maxDiscountPercent: number | null;
  version: number;
}

// Admin-gated (the read too — it carries canChangeCurrency).
export const getFarmSettings = () => apiGet<FarmSettings>("/account/settings");

export const updateFarmSettings = (body: UpdateFarmSettings, key?: string) =>
  apiPut<void>("/account/settings", body, key);

// #1146 — Owner-only, under the same Version as the settings above.
export const setConnectedApps = (allow: boolean, version: number) =>
  apiPut<void>("/account/connected-apps", { allow, version });

// --- Farm logo (#123) ---

// What the server STORED, not what the browser sent: the upload is rewritten
// without metadata, so the type and byte length can both differ from the file.
export interface FarmLogo {
  contentType: string;
  contentHash: string;
  width: number;
  height: number;
  byteLength: number;
  updatedAt: string;
}

// The formats the sanitizer accepts. SVG is refused server-side (it is a
// script container); listing them here only spares the user a round trip.
export const LOGO_ACCEPT = "image/png,image/jpeg,image/webp";

// Fetched through the API client rather than pointed at by an <img src>: the
// endpoint is behind the Authorization header, which an <img> cannot send. The
// caller renders the blob and revokes the URL.
export const getFarmLogo = () => apiGetBlob("/account/logo");

export const uploadFarmLogo = (file: File, key?: string) =>
  apiPutBytes<FarmLogo>(
    "/account/logo",
    file,
    // A browser that could not guess the type still uploads: the server sniffs
    // the bytes and this header is never read.
    file.type || "application/octet-stream",
    key,
  );

export const removeFarmLogo = (key?: string) => apiDelete<void>("/account/logo", key);

// --- Farm banner (#179) ---
//
// A second, independent branding image: a wide/hero picture shown full-size
// on the post-login splash, distinct from the small sidebar logo above. Same
// contract shape (raw-body PUT, blob-fetched GET, own upload cap from server
// config), just its own route and its own larger default cap.

export interface FarmBanner {
  contentType: string;
  contentHash: string;
  width: number;
  height: number;
  byteLength: number;
  updatedAt: string;
}

export const BANNER_ACCEPT = "image/png,image/jpeg,image/webp";

export const getFarmBanner = () => apiGetBlob("/account/banner");

export const uploadFarmBanner = (file: File, key?: string) =>
  apiPutBytes<FarmBanner>(
    "/account/banner",
    file,
    file.type || "application/octet-stream",
    key,
  );

export const removeFarmBanner = (key?: string) => apiDelete<void>("/account/banner", key);
