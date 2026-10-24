import { useEffect, useState } from "react";
import { api } from "../api/client";
import type { components } from "../api/schema";
import { problemTypeOf } from "../i18n/problems";
import { useSession } from "../session";
import type { Failure } from "./use-recipes";

export type Unit = components["schemas"]["UnitResponse"];
export type Ingredient = components["schemas"]["IngredientResponse"];
export type Tag = components["schemas"]["TagResponse"];
type Role = components["schemas"]["HouseholdRole"];

/** Adults and owners write recipes; children only read. The API enforces it (403); this only hides the controls. */
export function canEditRecipes(role: Role | undefined): boolean {
  return role === "Owner" || role === "Adult";
}

/** The signed-in user's role in the active household (from GET /api/households), or undefined. */
export function useActiveRole(): Role | undefined {
  const { session } = useSession();
  if (session.status !== "signedIn") return undefined;
  return session.households.find((h) => h.id === session.activeHouseholdId)?.role;
}

/** The global units for the unit selects. Empty while loading or if loading fails (the form then cannot pick a unit). */
export function useUnits(): Unit[] {
  const [units, setUnits] = useState<Unit[]>([]);
  useEffect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const { data } = await api.GET("/api/units");
        if (data && !cancelled) setUnits(data);
      } catch {
        // The form shows an empty select; saving a line then fails client-side with "pick a unit".
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);
  return units;
}

export type Result<T> = { ok: true; value: T } | { ok: false; failure: Failure };

const unreachable = { ok: false, failure: "unreachable" } as const;

/** Ingredients (global plus the household's) whose name contains `term`. */
export async function searchIngredients(householdId: string, term: string): Promise<Result<Ingredient[]>> {
  try {
    const { data, error } = await api.GET("/api/households/{householdId}/ingredients", {
      params: { path: { householdId: householdId }, query: { search: term, limit: 8 } },
    });
    return data ? { ok: true, value: data } : { ok: false, failure: { problemType: problemTypeOf(error) } };
  } catch {
    return unreachable;
  }
}

export async function createIngredient(
  householdId: string,
  body: { name: string; dimension: Ingredient["dimension"]; isStaple: boolean },
): Promise<Result<Ingredient>> {
  try {
    const { data, error } = await api.POST("/api/households/{householdId}/ingredients", {
      params: { path: { householdId: householdId } },
      body,
    });
    return data ? { ok: true, value: data } : { ok: false, failure: { problemType: problemTypeOf(error) } };
  } catch {
    return unreachable;
  }
}

export async function createTag(householdId: string, name: string): Promise<Result<Tag>> {
  try {
    const { data, error } = await api.POST("/api/households/{householdId}/tags", {
      params: { path: { householdId: householdId } },
      body: { name, kind: "Tag" },
    });
    return data ? { ok: true, value: data } : { ok: false, failure: { problemType: problemTypeOf(error) } };
  } catch {
    return unreachable;
  }
}
