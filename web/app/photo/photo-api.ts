import { api } from "../api/client";
import type { components } from "../api/schema";
import { problemTypeOf } from "../i18n/problems";
import type { MessageKey, Params } from "../i18n";
import { serverErrorsOf } from "../recipes/recipe-form";

// All shapes come from the generated client (rule 6).
export type PhotoImportDraft = components["schemas"]["RecipeImportResponse"];

export const maxPhotos = 4;
export const maxSourceName = 100;

const num = (value: unknown): number | null => (value === null || value === undefined || value === "" || Number.isNaN(Number(value)) ? null : Number(value));

export type PhotoFailure =
  | {
      kind: "problem";
      status: number;
      problemType: string | undefined;
      /** From the `Retry-After` header, seconds. */
      retryAfter: number | null;
      /** The count the server left, when the problem carries it. */
      remainingToday: number | null;
      /** Field errors of a 400 (`photos[2]` -> index 2, `photos` -> -1). */
      invalidPhoto: number | null;
    }
  | { kind: "unreachable" };

export type PhotoResult = { ok: true; draft: PhotoImportDraft } | { ok: false; failure: PhotoFailure };

/** The first photo a 400 names: `photos[i]` gives i, a general `photos` error gives -1, anything else null. */
export function invalidPhotoOf(fieldErrors: Record<string, string[]> | undefined): number | null {
  if (!fieldErrors) return null;
  for (const key of Object.keys(fieldErrors)) {
    const match = /^photos\[(\d+)\]$/.exec(key);
    if (match) return Number(match[1]);
  }
  return "photos" in fieldErrors ? -1 : null;
}

/**
 * POST `imports/photo`: the same multipart mechanism as the recipe picture upload (generated client, a body serializer that
 * builds the FormData). `signal` aborts the request; the caller treats an abort as "cancelled", not as a failure.
 */
export async function createPhotoImport(householdId: string, photos: File[], sourceName: string, signal: AbortSignal): Promise<PhotoResult> {
  try {
    const { data, error, response } = await api.POST("/api/households/{householdId}/imports/photo", {
      params: { path: { householdId } },
      // The generated schema types a binary part as `IFormFile`; the Files are what is sent.
      body: { photos: photos as unknown as components["schemas"]["IFormFile"][], sourceName: sourceName === "" ? null : sourceName },
      bodySerializer: () => {
        const form = new FormData();
        for (const photo of photos) form.append("photos", photo, photo.name);
        if (sourceName !== "") form.append("sourceName", sourceName);
        return form;
      },
      signal,
    });
    if (response.ok && data) return { ok: true, draft: data };
    const retry = num(response.headers.get("Retry-After"));
    return {
      ok: false,
      failure: {
        kind: "problem",
        status: response.status,
        problemType: problemTypeOf(error),
        retryAfter: retry,
        remainingToday: num((error as { remainingToday?: unknown } | undefined)?.remainingToday),
        invalidPhoto: response.status === 400 ? invalidPhotoOf(serverErrorsOf(error)) : null,
      },
    };
  } catch (e) {
    if (signal.aborted) throw e;
    return { ok: false, failure: { kind: "unreachable" } };
  }
}

const problem = (code: string) => `urn:foodplanner:problem:${code}`;

/** "about N seconds/minutes" for a Retry-After, as translated parts. */
export function waitKey(seconds: number): { key: MessageKey; params: Params } {
  return seconds < 90 ? { key: "photo.wait.seconds", params: { n: Math.max(1, Math.round(seconds)) } } : { key: "photo.wait.minutes", params: { n: Math.ceil(seconds / 60) } };
}

/** The translated message (key and params) for every kind of failure of the photo call. The server's text is never shown. */
export function photoFailureMessage(failure: PhotoFailure): { key: MessageKey; params?: Params; waitSeconds?: number; textFallback: boolean } {
  if (failure.kind === "unreachable") return { key: "problem.unreachable", textFallback: false };
  const type = failure.problemType;
  const by: Record<string, MessageKey> = {
    [problem("photo-import-limit-reached")]: "photo.problem.limitReached",
    [problem("photo-import-capacity")]: "photo.problem.capacity",
    [problem("photo-import-not-a-recipe")]: "photo.problem.notARecipe",
    [problem("photo-import-unreadable")]: "photo.problem.unreadable",
    [problem("photo-import-refused")]: "photo.problem.refused",
    [problem("photo-import-failed")]: "photo.problem.failed",
    [problem("photo-import-timeout")]: "photo.problem.timeout",
    [problem("photo-import-disabled")]: "photo.problem.disabled",
    [problem("photo-import-busy")]: "photo.problem.busy",
    [problem("import-limit")]: "import.problem.limit",
    [problem("kid-mode")]: "kid.problem.kidMode",
    [problem("forbidden")]: "problem.forbidden",
  };
  const textFallback = type !== undefined && ["not-a-recipe", "unreadable", "refused", "failed", "timeout", "limit-reached", "capacity", "disabled"].some((c) => type === problem(`photo-import-${c}`));
  if (type !== undefined && by[type]) return { key: by[type], textFallback };
  if (failure.status === 429) {
    if (failure.retryAfter !== null && failure.retryAfter > 0) return { key: "photo.problem.rateLimited", waitSeconds: failure.retryAfter, textFallback: false };
    return { key: "photo.problem.rateLimitedSoon", textFallback: false };
  }
  if (failure.status === 400 && failure.invalidPhoto !== null) {
    return failure.invalidPhoto >= 0
      ? { key: "photo.problem.invalid", params: { n: failure.invalidPhoto + 1 }, textFallback: false }
      : { key: "photo.problem.invalidAny", params: { max: maxPhotos }, textFallback: false };
  }
  if (failure.status === 403) return { key: "problem.forbidden", textFallback: false };
  if (failure.status === 401) return { key: "problem.unauthenticated", textFallback: false };
  return { key: "photo.problem.failed", textFallback: true };
}

export { num as photoNumber };
