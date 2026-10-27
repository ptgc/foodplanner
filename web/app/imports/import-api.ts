import { api } from "../api/client";
import type { components } from "../api/schema";
import type { I18n } from "../i18n";
import { problemKey, problemTypeOf } from "../i18n/problems";
import { serverErrorsOf } from "../recipes/recipe-form";

// All shapes come from the generated client (rule 6); nothing is hand-written here.
export type ImportDraft = components["schemas"]["RecipeImportResponse"];
export type ImportLine = components["schemas"]["ImportLineResponse"];
export type ImportSummary = components["schemas"]["RecipeImportSummaryResponse"];
export type ImportPatch = components["schemas"]["UpdateRecipeImportRequest"];
export type LineEdit = components["schemas"]["UpdateImportLineRequest"];

/** What went wrong: the HTTP status, the ProblemDetails type (translated on screen) and a ValidationProblem's field errors. */
export type ImportFailure =
  | { kind: "problem"; status: number; problemType: string | undefined; fieldErrors: Record<string, string[]> | undefined }
  | { kind: "unreachable" };

export type ImportResult<T> = { ok: true; value: T } | { ok: false; failure: ImportFailure };

type Answer<T> = { data?: T; error?: unknown; response: Response };

async function call<T>(run: () => Promise<Answer<T>>): Promise<ImportResult<T>> {
  try {
    const { data, error, response } = await run();
    if (response.ok && data !== undefined) return { ok: true, value: data };
    return {
      ok: false,
      failure: { kind: "problem", status: response.status, problemType: problemTypeOf(error), fieldErrors: serverErrorsOf(error) },
    };
  } catch {
    return { ok: false, failure: { kind: "unreachable" } };
  }
}

export const listImports = (householdId: string) =>
  call(() => api.GET("/api/households/{householdId}/imports", { params: { path: { householdId } } }));

export const createImport = (householdId: string, body: { url: string } | { text: string }) =>
  call(() =>
    api.POST("/api/households/{householdId}/imports", {
      params: { path: { householdId } },
      body: { url: "url" in body ? body.url : null, text: "text" in body ? body.text : null },
    }),
  );

export const getImport = (householdId: string, importId: string) =>
  call(() => api.GET("/api/households/{householdId}/imports/{importId}", { params: { path: { householdId, importId } } }));

export const updateImport = (householdId: string, importId: string, body: Partial<ImportPatch>) =>
  call(() =>
    api.PUT("/api/households/{householdId}/imports/{importId}", {
      params: { path: { householdId, importId } },
      body: { title: null, description: null, servings: null, prepMinutes: null, cookMinutes: null, steps: null, lines: null, ...body },
    }),
  );

export const saveImport = (householdId: string, importId: string) =>
  call(() => api.POST("/api/households/{householdId}/imports/{importId}/save", { params: { path: { householdId, importId } } }));

/** DELETE answers 204 without a body, so success is the status. */
export async function discardImport(householdId: string, importId: string): Promise<ImportResult<true>> {
  try {
    const { error, response } = await api.DELETE("/api/households/{householdId}/imports/{importId}", {
      params: { path: { householdId, importId } },
    });
    if (response.ok) return { ok: true, value: true };
    return {
      ok: false,
      failure: { kind: "problem", status: response.status, problemType: problemTypeOf(error), fieldErrors: undefined },
    };
  } catch {
    return { ok: false, failure: { kind: "unreachable" } };
  }
}

/**
 * The translated text for a failure. The server's English title and detail are never shown. A 429 (the per-user URL
 * import limit) and a 503 `import-busy` both say how to retry.
 */
export function importFailureText(t: I18n["t"], failure: ImportFailure): string {
  if (failure.kind === "unreachable") return t("problem.unreachable");
  if (failure.status === 429) return t("import.problem.rateLimited");
  return t(problemKey(failure.problemType));
}

/** True when the answer means "no recipe in that page", so the screen can offer to paste the text instead. */
export const isNoRecipeFound = (failure: ImportFailure): boolean =>
  failure.kind === "problem" && failure.problemType === "urn:foodplanner:problem:import-no-recipe-found";
