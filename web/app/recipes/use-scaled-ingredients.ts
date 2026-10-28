import { useEffect, useState } from "react";
import { api } from "../api/client";
import { useLocale } from "../i18n";
import type { Recipe, RecipeIngredient } from "./use-recipe";

export type Scaled = {
  /** The lines to show: the stored ones at the recipe's own servings, otherwise what the API scaled. */
  ingredients: RecipeIngredient[];
  /** A request for the chosen servings is in flight (the previous lines are still shown). */
  loading: boolean;
  failed: boolean;
};

/**
 * Ingredient lines for `servings`. The scaling and rounding happen on the server (Domain `RecipeScaling`, via
 * GET .../recipes/{id}/scaled); the client does no arithmetic on quantities. At the recipe's own servings the stored
 * lines are shown as written and nothing is requested.
 */
export function useScaledIngredients(householdId: string, recipe: Recipe, servings: number): Scaled {
  const own = servings === recipe.servings;
  const [result, setResult] = useState<{ servings: number; lines: RecipeIngredient[] } | null>(null);
  const [failedFor, setFailedFor] = useState<number | null>(null);
  const locale = useLocale();

  useEffect(() => {
    if (own) return;
    let cancelled = false;
    void (async () => {
      try {
        const { data } = await api.GET("/api/households/{householdId}/recipes/{recipeId}/scaled", {
          params: { path: { householdId, recipeId: recipe.id }, query: { servings } },
        });
        if (cancelled) return;
        if (data) {
          setResult({ servings, lines: data.ingredients });
          setFailedFor(null);
        } else setFailedFor(servings);
      } catch {
        if (!cancelled) setFailedFor(servings);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [householdId, recipe.id, servings, own, locale]);

  if (own) return { ingredients: recipe.ingredients, loading: false, failed: false };
  return {
    ingredients: result?.lines ?? recipe.ingredients,
    loading: result?.servings !== servings && failedFor !== servings,
    failed: failedFor === servings,
  };
}
