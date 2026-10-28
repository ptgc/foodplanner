import { useEffect, useState } from "react";
import { api } from "../api/client";
import type { components } from "../api/schema";
import { useLocale } from "../i18n";
import { problemTypeOf } from "../i18n/problems";

export type Recipe = components["schemas"]["RecipeResponse"];
export type RecipeIngredient = components["schemas"]["RecipeIngredientResponse"];

export type RecipeState =
  | { kind: "ok"; recipe: Recipe }
  | { kind: "notFound" }
  | { kind: "error"; message: "unreachable" | string | undefined };

/** Reads one recipe. `null` while loading. Shared by the detail page and the cooking view. */
export function useRecipe(householdId: string, id: string): RecipeState | null {
  const [state, setState] = useState<RecipeState | null>(null);
  const locale = useLocale(); // ingredient, unit and tag names are in the active language: read again on a switch (T-078)

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      let next: RecipeState;
      try {
        const { data, error, response } = await api.GET("/api/households/{householdId}/recipes/{recipeId}", {
          params: { path: { householdId, recipeId: id } },
        });
        if (data) next = { kind: "ok", recipe: data };
        else if (response.status === 404) next = { kind: "notFound" };
        else next = { kind: "error", message: problemTypeOf(error) };
      } catch {
        next = { kind: "error", message: "unreachable" };
      }
      if (!cancelled) setState(next);
    })();
    return () => {
      cancelled = true;
    };
  }, [householdId, id, locale]);

  return state;
}
