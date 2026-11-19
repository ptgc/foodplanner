import { useEffect, useState } from "react";
import { api } from "../api/client";
import type { components } from "../api/schema";

export type PickOptions = components["schemas"]["PickOptionsResponse"];
export type PickOption = components["schemas"]["PickOptionResponse"];

/**
 * Which recipes fit a day of the week under the household's meal mix rules (T-090). `options` is `null` while loading or when the
 * lookup failed: the adult planner then greys nothing out (the server stays the authority for kids; an adult may add anything).
 * `loading` tells the two apart; `refresh` reads again (after a pick was refused, the answer has changed).
 */
export function usePickOptions(householdId: string, weekStart: string, date: string) {
  const [state, setState] = useState<{ key: string; options: PickOptions | null } | null>(null);
  const [attempt, setAttempt] = useState(0);
  const key = `${householdId}/${weekStart}/${date}/${attempt}`;

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      let options: PickOptions | null = null;
      try {
        const { data } = await api.GET("/api/households/{householdId}/meal-plans/{weekStart}/pick-options", {
          params: { path: { householdId, weekStart }, query: { date } },
        });
        options = data ?? null;
      } catch {
        options = null;
      }
      if (!cancelled) setState({ key, options });
    })();
    return () => {
      cancelled = true;
    };
  }, [householdId, weekStart, date, key]);

  const options = state?.key === key ? state.options : null;
  const byRecipe = new Map((options?.recipes ?? []).map((o) => [o.recipeId, o]));
  return { options, byRecipe, loading: state?.key !== key, refresh: () => setAttempt((a) => a + 1) };
}
