import type { MessageKey } from "./index";

/**
 * ProblemDetails `type` codes (urn:foodplanner:problem:*, RFC 9457) -> translated message keys. The server's
 * `title`/`detail` are English and never shown; add a code here when a screen starts to rely on it.
 */
const problemKeys: Record<string, MessageKey> = {
  "urn:foodplanner:problem:login-failed": "problem.loginFailed",
  "urn:foodplanner:problem:rate-limited": "problem.rateLimited",
  "urn:foodplanner:problem:unauthenticated": "problem.unauthenticated",
  "urn:foodplanner:problem:forbidden": "problem.forbidden",
  "urn:foodplanner:problem:forbidden-origin": "problem.forbiddenOrigin",
  "urn:foodplanner:problem:antiforgery-invalid": "problem.antiforgeryInvalid",
  "urn:foodplanner:problem:already-member": "problem.alreadyMember",
  "urn:foodplanner:problem:invite-invalid": "problem.inviteInvalid",
  "urn:foodplanner:problem:confirmation-invalid": "problem.confirmationInvalid",
  "urn:foodplanner:problem:meal-plan-entry-not-found": "problem.mealPlanEntryNotFound",
  "urn:foodplanner:problem:meal-plan-entry-conflict": "problem.mealPlanEntryConflict",
  "urn:foodplanner:problem:recipe-in-use": "recipe.problem.inUse",
  "urn:foodplanner:problem:recipe-conflict": "recipe.problem.conflict",
  "urn:foodplanner:problem:recipe-not-found": "recipe.problem.notFound",
  "urn:foodplanner:problem:ingredient-exists": "recipe.problem.ingredientExists",
  "urn:foodplanner:problem:ingredient-in-use": "ingredients.problem.inUse",
  "urn:foodplanner:problem:ingredient-not-found": "ingredients.problem.notFound",
  "urn:foodplanner:problem:ingredient-conflict": "ingredients.problem.conflict",
  "urn:foodplanner:problem:ingredient-global": "ingredients.problem.global",
  "urn:foodplanner:problem:ingredient-merge-into-self": "ingredients.problem.mergeIntoSelf",
  "urn:foodplanner:problem:tag-exists": "recipe.problem.tagExists",
  "urn:foodplanner:problem:image-too-large": "recipe.problem.imageTooLarge",
  "urn:foodplanner:problem:import-url-not-allowed": "import.problem.urlNotAllowed",
  "urn:foodplanner:problem:import-unsupported-content": "import.problem.unsupportedContent",
  "urn:foodplanner:problem:import-too-large": "import.problem.tooLarge",
  "urn:foodplanner:problem:import-no-recipe-found": "import.problem.noRecipeFound",
  "urn:foodplanner:problem:import-fetch-failed": "import.problem.fetchFailed",
  "urn:foodplanner:problem:import-fetch-timeout": "import.problem.fetchTimeout",
  "urn:foodplanner:problem:import-busy": "import.problem.busy",
  "urn:foodplanner:problem:import-limit": "import.problem.limit",
  "urn:foodplanner:problem:import-not-draft": "import.problem.notDraft",
  "urn:foodplanner:problem:import-not-found": "import.problem.notFound",
  "urn:foodplanner:problem:import-conflict": "import.problem.conflict",
  "urn:foodplanner:problem:last-owner": "household.problem.lastOwner",
  "urn:foodplanner:problem:member-not-found": "household.problem.memberNotFound",
  "urn:foodplanner:problem:cannot-remove-self": "household.problem.cannotRemoveSelf",
  "urn:foodplanner:problem:owner-cannot-be-removed": "household.problem.ownerCannotBeRemoved",
  "urn:foodplanner:problem:invite-not-found": "household.problem.inviteNotFound",
  "urn:foodplanner:problem:weekly-grocery-exists": "household.weekly.problem.exists",
  "urn:foodplanner:problem:weekly-grocery-limit": "household.weekly.problem.limit",
  "urn:foodplanner:problem:weekly-grocery-not-found": "household.weekly.problem.notFound",
  "urn:foodplanner:problem:weekly-grocery-conflict": "household.weekly.problem.conflict",
  "urn:foodplanner:problem:pantry-conflict": "pantry.problem.conflict",
};

/** The `type` of a ProblemDetails body, if the value looks like one. */
export function problemTypeOf(body: unknown): string | undefined {
  if (typeof body !== "object" || body === null) return undefined;
  const type = (body as { type?: unknown }).type;
  return typeof type === "string" ? type : undefined;
}

/** Message key for a problem type; unknown or missing types give the generic message. */
export function problemKey(type: string | undefined): MessageKey {
  return (type !== undefined ? problemKeys[type] : undefined) ?? "problem.generic";
}
