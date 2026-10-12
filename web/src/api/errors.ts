import type { ProblemDetails } from "./types";
import i18n from "../i18n";

export class ApiError extends Error {
  constructor(
    public status: number,
    public title: string,
    message: string,
  ) {
    super(message);
    this.name = "ApiError";
  }
}

export async function parseError(res: Response): Promise<ApiError> {
  let title = res.statusText;
  let detail = res.statusText;
  try {
    const body = (await res.json()) as ProblemDetails & {
      errors?: Record<string, string[]>;
      errorCodes?: Record<string, (string | null)[]>;
    };
    title = body.title ?? title;
    // ValidationProblem carries an errors map and usually no detail — flatten
    // it so the user sees which field is wrong, not just "Bad Request".
    if (body.errors && Object.keys(body.errors).length > 0) {
      // Per-field, index-aligned: a slot with an explicit code whose catalog key
      // exists renders the translated message; otherwise the English server
      // message at the SAME index (uncoded slots, or codes without a catalog key,
      // keep their English — matching the additive, explicit-only #45 contract).
      const parts: string[] = [];
      for (const [field, messages] of Object.entries(body.errors)) {
        const codes = body.errorCodes?.[field] ?? [];
        messages.forEach((msg, i) => {
          const code = codes[i];
          // `defaultValue: msg` IS the fallback: a known catalog key wins, an
          // unknown code (or an uncoded/null slot) renders the server English
          // message. This also satisfies i18next's typed t() — a runtime string
          // key needs the defaultValue overload (a bare i18n.t(dynamicKey) is a
          // type error under CustomTypeOptions). keySeparator:false makes the
          // dotted `errors:Me.Language.Format` a literal key, not a nested path.
          parts.push(code ? i18n.t(`errors:${code}`, { defaultValue: msg }) : msg);
        });
      }
      detail = parts.join(" ");
    } else {
      // #612 — a plain domain-error ProblemDetails (Error.Domain) carries no
      // errors map, just a stable code AS its title. Same catalog, same
      // defaultValue fallback as the per-field lookup above: a coded screen
      // (e.g. EggLot.AssignedFlocksInsufficientStock) renders translated, an
      // uncatalogued title keeps the server's English detail.
      const serverDetail = body.detail ?? detail;
      detail = title ? i18n.t(`errors:${title}`, { defaultValue: serverDetail }) : serverDetail;
    }
  } catch {
    // non-JSON body — keep status text
  }
  return new ApiError(res.status, title, detail);
}
