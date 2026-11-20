import { api } from "../api/client";
import type { components } from "../api/schema";
import { problemTypeOf } from "../i18n/problems";

// The "Add to Oda cart" flow (T-054, spec T-053). Everything about Oda comes from the API and lives in React state only:
// nothing from Oda (code, state, products, tokens) is ever written to browser storage.

export type OdaCart = components["schemas"]["OdaCartResponse"];
export type OdaAdded = components["schemas"]["OdaAddedItemResponse"];

const prefix = "urn:foodplanner:problem:";

export type OdaStartResult =
  | { kind: "redirect"; url: string }
  | { kind: "refused"; reason: "not-configured" | "nothing-to-add" | "too-many" | "rate-limited" | "forbidden" | "failed" };

/** The browser navigation, a seam so tests can watch it. A full-page navigation: Oda is another origin. */
export const navigation = { go: (url: string) => window.location.assign(url) };

/** Only an http(s) address is followed or linked: Oda text must never become a script address. */
export function safeHttpUrl(value: unknown): string | null {
  if (typeof value !== "string") return null;
  try {
    const url = new URL(value);
    return url.protocol === "https:" || url.protocol === "http:" ? url.href : null;
  } catch {
    return null;
  }
}

function refusal(status: number, body: unknown): Extract<OdaStartResult, { kind: "refused" }> {
  const type = problemTypeOf(body);
  if (type === `${prefix}oda-not-configured`) return { kind: "refused", reason: "not-configured" };
  if (type === `${prefix}oda-nothing-to-add`) return { kind: "refused", reason: "nothing-to-add" };
  if (type === `${prefix}oda-too-many-items`) return { kind: "refused", reason: "too-many" };
  if (status === 404) return { kind: "refused", reason: "not-configured" };
  if (status === 429) return { kind: "refused", reason: "rate-limited" };
  if (status === 403 || status === 401) return { kind: "refused", reason: "forbidden" };
  return { kind: "refused", reason: "failed" };
}

/** Step 1: asks the API for Oda's sign-in address. The caller navigates to it. The list itself is read by the server. */
export async function startOda(householdId: string, weekStart: string): Promise<OdaStartResult> {
  try {
    const { data, response, error } = await api.POST("/api/households/{householdId}/shopping-lists/{weekStart}/oda/start", {
      params: { path: { householdId, weekStart } },
      body: { confirmed: true },
    });
    if (response.ok) {
      const url = safeHttpUrl(data?.authorizeUrl);
      return url ? { kind: "redirect", url } : { kind: "refused", reason: "failed" };
    }
    return refusal(response.status, error);
  } catch {
    return { kind: "refused", reason: "failed" };
  }
}

export type OdaCompleteResult =
  | { kind: "result"; cart: OdaCart }
  | {
      kind: "failed";
      reason: "state-invalid" | "nothing-to-add" | "rate-limited" | "forbidden" | "not-configured" | "unavailable" | "failed";
      /** Lines already in the cart before a 502 (so the user does not add them twice). */
      added: OdaAdded[];
      /** False only when the API says the connection could not be closed. */
      connectionClosed: boolean | null;
      /** The text export is offered after any failure that is not a plain refusal. */
      offerText: boolean;
    };

const asString = (v: unknown): string | null => (typeof v === "string" ? v : null);

/** The `added` extension of the 502 body (not part of the generated ProblemDetails type), checked field by field. */
function addedOf(body: unknown): OdaAdded[] {
  const list = typeof body === "object" && body !== null ? (body as { added?: unknown }).added : undefined;
  if (!Array.isArray(list)) return [];
  const lines: OdaAdded[] = [];
  for (const raw of list) {
    if (typeof raw !== "object" || raw === null) continue;
    const l = raw as Record<string, unknown>;
    const name = asString(l.name);
    if (name === null) continue;
    lines.push({
      itemId: asString(l.itemId) ?? "",
      name,
      productName: asString(l.productName) ?? "",
      quantity: typeof l.quantity === "number" || typeof l.quantity === "string" ? l.quantity : 1,
    });
  }
  return lines;
}

function failure(status: number, body: unknown): Extract<OdaCompleteResult, { kind: "failed" }> {
  const type = problemTypeOf(body);
  const closedRaw = typeof body === "object" && body !== null ? (body as { connectionClosed?: unknown }).connectionClosed : undefined;
  const base = { added: [] as OdaAdded[], connectionClosed: typeof closedRaw === "boolean" ? closedRaw : null, offerText: true };
  if (type === `${prefix}oda-state-invalid`) return { kind: "failed", reason: "state-invalid", ...base, offerText: false };
  if (type === `${prefix}oda-nothing-to-add`) return { kind: "failed", reason: "nothing-to-add", ...base, offerText: false };
  if (type === `${prefix}oda-not-configured` || status === 404) return { kind: "failed", reason: "not-configured", ...base, offerText: false };
  if (status === 429) return { kind: "failed", reason: "rate-limited", ...base };
  if (status === 403 || status === 401) return { kind: "failed", reason: "forbidden", ...base, offerText: false };
  if (status === 502 || type === `${prefix}oda-unavailable`) return { kind: "failed", reason: "unavailable", ...base, added: addedOf(body) };
  return { kind: "failed", reason: "failed", ...base };
}

/**
 * Step 3: hands what Oda sent back to the API, which fills the cart. Quantities are deltas, so this is called at most
 * once per redirect and never retried (the caller guards that); a network error is reported, not repeated.
 */
export async function completeOda(
  householdId: string,
  weekStart: string,
  params: { state: string; code: string | null; error: string | null },
): Promise<OdaCompleteResult> {
  try {
    const { data, response, error } = await api.POST("/api/households/{householdId}/shopping-lists/{weekStart}/oda/cart", {
      params: { path: { householdId, weekStart } },
      body: { state: params.state, code: params.code, error: params.error },
    });
    if (response.ok && data) return { kind: "result", cart: data };
    return failure(response.status, error);
  } catch {
    return { kind: "failed", reason: "failed", added: [], connectionClosed: null, offerText: true };
  }
}

// Where the callback page finds the week again. This is Foodplanner's own data (which household and Monday), not Oda's;
// it is cleared as soon as the callback reads it. The code and state are never stored.
const pendingKey = "foodplanner.oda.pending";

export type OdaPending = { householdId: string; weekStart: string };

export function rememberPending(pending: OdaPending): void {
  try {
    window.sessionStorage.setItem(pendingKey, JSON.stringify(pending));
  } catch {
    // Blocked storage: the callback then cannot find the week and says so.
  }
}

/** Reads and clears the pending week. */
export function takePending(): OdaPending | null {
  try {
    const raw = window.sessionStorage.getItem(pendingKey);
    window.sessionStorage.removeItem(pendingKey);
    if (!raw) return null;
    const v = JSON.parse(raw) as Partial<OdaPending>;
    return typeof v.householdId === "string" && typeof v.weekStart === "string" ? { householdId: v.householdId, weekStart: v.weekStart } : null;
  } catch {
    return null;
  }
}
