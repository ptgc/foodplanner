import { useCallback, useEffect, useMemo, useState } from "react";
import { api } from "../api/client";
import type { components } from "../api/schema";
import { problemTypeOf } from "../i18n/problems";
import { toListQuery, type RecipeFilters } from "./filters";

export type RecipeSummary = components["schemas"]["RecipeSummaryResponse"];
export type Tag = components["schemas"]["TagResponse"];

export const pageSize = 12;

/** A ProblemDetails type code (translated on screen) or "unreachable" (network failure). */
export type Failure = { problemType: string | undefined } | "unreachable";

type Request = { householdId: string; filters: RecipeFilters; attempt: number };

type Loaded = {
  request: Request;
  result: { status: "ok"; items: RecipeSummary[]; total: number } | { status: "error"; failure: Failure };
};

export type RecipeList =
  | { status: "loading" }
  | { status: "error"; failure: Failure }
  | {
      status: "ok";
      items: RecipeSummary[];
      total: number;
      hasMore: boolean;
      loadingMore: boolean;
      /** Set when the last "show more" failed; the items already shown stay. */
      moreFailure: Failure | null;
    };

/**
 * The generated schema does not declare the `{householdId}` route parameter on the list endpoints (the OpenAPI
 * generator only lists `{recipeId}` etc.), so its path type is `undefined`. The client still fills the template
 * from `params.path`; this is the one cast, and it goes away when the contract declares the parameter.
 */
export function householdPath(householdId: string): never {
  return { householdId } as never;
}

type Page = { ok: true; items: RecipeSummary[]; total: number } | { ok: false; failure: Failure };

async function fetchPage(request: Request, offset: number): Promise<Page> {
  const { data, error } = await api.GET("/api/households/{householdId}/recipes", {
    params: { path: householdPath(request.householdId), query: toListQuery(request.filters, pageSize, offset) },
  });
  if (!data) return { ok: false, failure: { problemType: problemTypeOf(error) } };
  return { ok: true, items: data.items, total: Number(data.total) };
}

/**
 * The recipes matching the filters, a page at a time. A change of household or filters starts over (and shows
 * "loading"); `loadMore` appends the next page (offset = items already shown).
 */
export function useRecipeList(householdId: string, filters: RecipeFilters) {
  const [attempt, setAttempt] = useState(0);
  const request = useMemo<Request>(() => ({ householdId, filters, attempt }), [householdId, filters, attempt]);
  const [loaded, setLoaded] = useState<Loaded | null>(null);
  const [more, setMore] = useState<{ request: Request; busy: boolean; failure: Failure | null } | null>(null);

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      let result: Loaded["result"];
      try {
        const page = await fetchPage(request, 0);
        result = page.ok ? { status: "ok", items: page.items, total: page.total } : { status: "error", failure: page.failure };
      } catch {
        result = { status: "error", failure: "unreachable" };
      }
      if (!cancelled) setLoaded({ request, result });
    })();
    return () => {
      cancelled = true;
    };
  }, [request]);

  const current = loaded?.request === request ? loaded.result : null;
  const moreNow = more?.request === request ? more : null;

  const loadMore = useCallback(async () => {
    if (current?.status !== "ok" || moreNow?.busy) return;
    setMore({ request, busy: true, failure: null });
    let failure: Failure | null = null;
    try {
      const page = await fetchPage(request, current.items.length);
      if (!page.ok) failure = page.failure;
      else {
        setLoaded((prev) =>
          prev?.request === request && prev.result.status === "ok"
            ? { request, result: { status: "ok", items: [...prev.result.items, ...page.items], total: page.total } }
            : prev,
        );
      }
    } catch {
      failure = "unreachable";
    }
    setMore((prev) => (prev?.request === request ? { request, busy: false, failure } : prev));
  }, [current, moreNow?.busy, request]);

  const retry = useCallback(() => setAttempt((a) => a + 1), []);

  const list: RecipeList = !current
    ? { status: "loading" }
    : current.status === "error"
      ? { status: "error", failure: current.failure }
      : {
          status: "ok",
          items: current.items,
          total: current.total,
          hasMore: current.items.length < current.total,
          loadingMore: moreNow?.busy ?? false,
          moreFailure: moreNow?.failure ?? null,
        };
  return { list, loadMore, retry };
}

/** The tags and categories of the household (global plus own) for the filter chips. Empty if loading fails. */
export function useTags(householdId: string): Tag[] {
  const [loaded, setLoaded] = useState<{ householdId: string; tags: Tag[] } | null>(null);
  useEffect(() => {
    let cancelled = false;
    void (async () => {
      let tags: Tag[] = [];
      try {
        const { data } = await api.GET("/api/households/{householdId}/tags", {
          params: { path: householdPath(householdId) },
        });
        tags = data ?? [];
      } catch {
        // The grid still works without the chips.
      }
      if (!cancelled) setLoaded({ householdId, tags });
    })();
    return () => {
      cancelled = true;
    };
  }, [householdId]);
  return loaded?.householdId === householdId ? loaded.tags : [];
}
