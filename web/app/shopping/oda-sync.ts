import { api } from "../api/client";
import type { components } from "../api/schema";
import { call } from "../household/call";
import { problemTypeOf } from "../i18n/problems";
import { safeHttpUrl } from "./oda";

// "Sync with Oda" (T-130, spec T-130-oda-sync). Everything about Oda comes from the API and lives in React state only: the review (product
// names and ids), the code and the state are never written to browser storage. The generated client is the only API client.

export type OdaSyncReview = components["schemas"]["OdaSyncReviewResponse"];
export type OdaSyncRow = OdaSyncReview["rows"][number];
export type OdaSyncOption = OdaSyncRow["options"][number];

const prefix = "urn:foodplanner:problem:";

export type OdaSyncOptions = { includeStaples: boolean; includeExisting: boolean; offset: number };

export type OdaSyncStartResult =
  | { kind: "ready"; url: string; ingredientCount: number; notCheckedCount: number }
  | { kind: "refused"; reason: "not-configured" | "nothing-to-sync" | "rate-limited" | "forbidden" | "failed" };

/** Step 1 (and the preview of the confirmation): the API counts the ingredients and returns Oda's sign-in address. No call to Oda is made. */
export async function startSync(householdId: string, options: OdaSyncOptions): Promise<OdaSyncStartResult> {
  try {
    const { data, response, error } = await api.POST("/api/households/{householdId}/oda-sync/start", {
      params: { path: { householdId } },
      body: { confirmed: true, includeStaples: options.includeStaples, includeExisting: options.includeExisting, offset: options.offset },
    });
    if (response.ok) {
      const url = safeHttpUrl(data?.authorizeUrl);
      return url && data ? { kind: "ready", url, ingredientCount: Number(data.ingredientCount), notCheckedCount: Number(data.notCheckedCount) } : { kind: "refused", reason: "failed" };
    }
    const type = problemTypeOf(error);
    if (type === `${prefix}oda-sync-nothing-to-sync`) return { kind: "refused", reason: "nothing-to-sync" };
    if (type === `${prefix}oda-not-configured` || response.status === 404) return { kind: "refused", reason: "not-configured" };
    if (response.status === 429) return { kind: "refused", reason: "rate-limited" };
    if (response.status === 403 || response.status === 401) return { kind: "refused", reason: "forbidden" };
    return { kind: "refused", reason: "failed" };
  } catch {
    return { kind: "refused", reason: "failed" };
  }
}

export type OdaSyncOutcome =
  | { kind: "review"; review: OdaSyncReview }
  | { kind: "failed"; reason: "state-invalid" | "rate-limited" | "forbidden" | "not-configured" | "nothing-to-sync" | "unavailable" | "failed"; connectionClosed: boolean | null };

/**
 * Step 3: hands what Oda sent back to the API, which searches and closes the connection in this one request. Called once per page load by
 * the callback page and never retried.
 */
export async function reviewSync(householdId: string, params: { state: string; code: string | null; error: string | null }): Promise<OdaSyncOutcome> {
  try {
    const { data, response, error } = await api.POST("/api/households/{householdId}/oda-sync/review", {
      params: { path: { householdId } },
      body: { state: params.state, code: params.code, error: params.error },
    });
    if (response.ok && data) return { kind: "review", review: data };
    const type = problemTypeOf(error);
    const closedRaw = typeof error === "object" && error !== null ? (error as { connectionClosed?: unknown }).connectionClosed : undefined;
    const connectionClosed = typeof closedRaw === "boolean" ? closedRaw : null;
    const failed = (reason: Extract<OdaSyncOutcome, { kind: "failed" }>["reason"]): OdaSyncOutcome => ({ kind: "failed", reason, connectionClosed });
    if (type === `${prefix}oda-state-invalid`) return failed("state-invalid");
    if (type === `${prefix}oda-sync-nothing-to-sync`) return failed("nothing-to-sync");
    if (type === `${prefix}oda-not-configured` || response.status === 404) return failed("not-configured");
    if (response.status === 429) return failed("rate-limited");
    if (response.status === 403 || response.status === 401) return failed("forbidden");
    if (response.status === 502 || type === `${prefix}oda-unavailable`) return failed("unavailable");
    return failed("failed");
  } catch {
    return { kind: "failed", reason: "failed", connectionClosed: null };
  }
}

export type OdaSyncSave = { ingredientId: string; odaProductId: number; replace: boolean };

/** Saves the ticked rows (ids only) with the bulk PUT. Nothing calls Oda; the connection is already closed. */
export function saveSyncChoices(householdId: string, items: OdaSyncSave[]) {
  return call(() =>
    api.PUT("/api/households/{householdId}/oda-preferences", {
      params: { path: { householdId } },
      body: { items },
    }),
  );
}
