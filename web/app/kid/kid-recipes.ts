import { useCallback, useEffect, useState } from "react";
import { api } from "../api/client";
import { useLocale } from "../i18n";
import { problemTypeOf } from "../i18n/problems";
import type { RecipeSummary } from "../recipes/use-recipes";

/** The stable key of the global tag "Barnevennlig" (TagResponse.key, the same in every language). */
export const KID_FRIENDLY_TAG_KEY = "barnevennlig";

export function isKidFriendly(recipe: Pick<RecipeSummary, "tags">): boolean {
  return recipe.tags.some((tag) => tag.isGlobal && tag.key === KID_FRIENDLY_TAG_KEY);
}

/** Barnevennlig dishes first (Q-020), everything else after; the server's order is kept within each group. */
export function kidFriendlyFirst<T extends Pick<RecipeSummary, "tags">>(items: readonly T[]): T[] {
  return [...items.filter(isKidFriendly), ...items.filter((r) => !isKidFriendly(r))];
}

const pageLimit = 100; // the API's maximum
const maxPages = 5; // 500 dishes is far more than a family cooks; stops a runaway loop

export type KidRecipes =
  | { status: "loading" }
  | { status: "error"; problemType: string | undefined; unreachable: boolean }
  | { status: "ok"; items: RecipeSummary[] };

/**
 * All dishes of the household, Barnevennlig first. The kid feed has no search or filters, so it reads the pages (100
 * each) until everything is in, then orders them, which is why the ordering is right even for a large household.
 */
export function useKidRecipes(householdId: string) {
  const locale = useLocale();
  const [attempt, setAttempt] = useState(0);
  const [state, setState] = useState<{ key: string; value: KidRecipes } | null>(null);
  const key = `${householdId}/${locale}/${attempt}`;

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      let value: KidRecipes;
      try {
        const items: RecipeSummary[] = [];
        value = { status: "ok", items };
        for (let page = 0; page < maxPages; page++) {
          const { data, error } = await api.GET("/api/households/{householdId}/recipes", {
            params: { path: { householdId }, query: { limit: pageLimit, offset: items.length } },
          });
          if (!data) {
            value = { status: "error", problemType: problemTypeOf(error), unreachable: false };
            break;
          }
          items.push(...data.items);
          if (items.length >= Number(data.total) || data.items.length === 0) break;
        }
        if (value.status === "ok") value = { status: "ok", items: kidFriendlyFirst(items) };
      } catch {
        value = { status: "error", problemType: undefined, unreachable: true };
      }
      if (!cancelled) setState({ key, value });
    })();
    return () => {
      cancelled = true;
    };
  }, [householdId, key]);

  const retry = useCallback(() => setAttempt((a) => a + 1), []);
  return { recipes: state?.key === key ? state.value : ({ status: "loading" } as KidRecipes), retry };
}
