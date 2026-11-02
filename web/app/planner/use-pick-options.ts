import { useEffect, useState } from "react";
import { api } from "../api/client";
import type { components } from "../api/schema";

export type PickOptions = components["schemas"]["PickOptionsResponse"];
export type PickOption = components["schemas"]["PickOptionResponse"];

/**
 * Which recipes fit a day of the week under the household's meal mix rules (T-090). `null` while loading or when the lookup failed:
 * the planner then greys nothing out (the server stays the authority for kids; an adult may add anything).
 */
export function usePickOptions(householdId: string, weekStart: string, date: string) {
  const [options, setOptions] = useState<PickOptions | null>(null);

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const { data } = await api.GET("/api/households/{householdId}/meal-plans/{weekStart}/pick-options", {
          params: { path: { householdId, weekStart }, query: { date } },
        });
        if (!cancelled) setOptions(data ?? null);
      } catch {
        if (!cancelled) setOptions(null);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [householdId, weekStart, date]);

  const byRecipe = new Map((options?.recipes ?? []).map((o) => [o.recipeId, o]));
  return { options, byRecipe };
}
