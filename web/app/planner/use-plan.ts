import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { api } from "../api/client";
import type { components } from "../api/schema";
import type { MessageKey } from "../i18n";
import { problemKey, problemTypeOf } from "../i18n/problems";
import type { RecipeSummary } from "../recipes/use-recipes";

export type PlanEntry = components["schemas"]["MealPlanEntryResponse"];

export const minServings = 1;
export const maxServings = 50;

/** A ProblemDetails type code (translated on screen) or "unreachable" (network failure). */
export type Failure = { problemType: string | undefined } | "unreachable";

/** What a write did: ok, or the translated reason it was refused. */
export type WriteResult = { ok: true } | { ok: false; message: MessageKey };

export type Images = Record<string, { thumbUrl: string | null; cardUrl: string | null }>;

export type Plan =
  | { status: "loading" }
  | { status: "error"; failure: Failure }
  /** `locked`: an adult froze the week, so kids cannot change it (T-026). */
  | { status: "ok"; entries: PlanEntry[]; locked: boolean };

type Loaded = {
  key: string;
  result: { status: "ok"; entries: PlanEntry[]; locked: boolean } | { status: "error"; failure: Failure };
};

/**
 * Message for a refused write. The ProblemDetails type wins (403 forbidden, 404 entry gone, 409 conflict); a
 * validation problem (400, no urn type) gets its own text.
 */
export function writeMessage(status: number, type: string | undefined): MessageKey {
  if (type !== undefined && type.startsWith("urn:foodplanner:problem:")) return problemKey(type);
  if (status === 400) return "planner.badRequest";
  if (status === 403) return "problem.forbidden";
  if (status === 404) return "problem.mealPlanEntryNotFound";
  if (status === 409) return "problem.mealPlanEntryConflict";
  return "planner.actionFailed";
}

const byDateThenOrder = (a: PlanEntry, b: PlanEntry) => (a.date < b.date ? -1 : a.date > b.date ? 1 : 0);

/**
 * The household's plan for one ISO week, and the writes on it. Every write updates the shown plan from the
 * server's answer; a 404/409 re-reads the week, since somebody else changed it.
 */
export function usePlan(householdId: string, weekStart: string) {
  const key = `${householdId}/${weekStart}`;
  const [attempt, setAttempt] = useState(0);
  const [loaded, setLoaded] = useState<Loaded | null>(null);
  const [images, setImages] = useState<Images>({});

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      let result: Loaded["result"];
      try {
        const { data, error } = await api.GET("/api/households/{householdId}/meal-plans/{weekStart}", {
          params: { path: { householdId, weekStart } },
        });
        result = data
          ? { status: "ok", entries: data.entries, locked: data.locked === true }
          : { status: "error", failure: { problemType: problemTypeOf(error) } };
      } catch {
        result = { status: "error", failure: "unreachable" };
      }
      // A background re-read (somebody else changed the week) that fails keeps what is on screen.
      if (!cancelled)
        setLoaded((prev) =>
          result.status === "error" && attempt > 0 && prev?.key === key && prev.result.status === "ok" ? prev : { key, result },
        );
    })();
    return () => {
      cancelled = true;
    };
  }, [householdId, weekStart, key, attempt]);

  const current = loaded?.key === key ? loaded.result : null;
  const entries = current?.status === "ok" ? current.entries : null;

  // Plan entries carry no image urls; the recipe has them. One lookup per recipe, failures show the placeholder.
  const recipeIds = useMemo(() => [...new Set((entries ?? []).map((e) => e.recipeId))], [entries]);
  const missing = recipeIds.filter((id) => !(id in images)).join(",");
  useEffect(() => {
    if (missing === "") return;
    let cancelled = false;
    void Promise.all(
      missing.split(",").map(async (recipeId) => {
        try {
          const { data } = await api.GET("/api/households/{householdId}/recipes/{recipeId}", {
            params: { path: { householdId, recipeId } },
          });
          return [recipeId, { thumbUrl: data?.image?.thumbUrl ?? null, cardUrl: data?.image?.cardUrl ?? null }] as const;
        } catch {
          return [recipeId, { thumbUrl: null, cardUrl: null }] as const;
        }
      }),
    ).then((found) => {
      if (!cancelled) setImages((prev) => ({ ...prev, ...Object.fromEntries(found) }));
    });
    return () => {
      cancelled = true;
    };
  }, [missing, householdId]);

  const reload = useCallback(() => setAttempt((a) => a + 1), []);

  // Other people (a kid on the tablet, a parent on the phone) change the week: read it again when the hub says so.
  const weekRef = useRef(weekStart);
  useEffect(() => {
    weekRef.current = weekStart;
  });
  useEffect(() => {
    // SignalR is a big dependency and only needed once the screen is up: its own chunk, loaded after the first paint.
    let stopped = false;
    let stop: (() => Promise<void>) | undefined;
    void import("./plan-realtime").then(({ connectPlanRealtime }) => {
      if (stopped) return;
      stop = connectPlanRealtime(householdId, { getWeek: () => weekRef.current, onChange: reload }).stop;
    });
    return () => {
      stopped = true;
      void stop?.();
    };
  }, [householdId, reload]);

  const change = useCallback(
    (update: (list: PlanEntry[]) => PlanEntry[]) =>
      setLoaded((prev) =>
        prev?.key === key && prev.result.status === "ok"
          ? { key, result: { ...prev.result, entries: update(prev.result.entries).sort(byDateThenOrder) } }
          : prev,
      ),
    [key],
  );

  const write = useCallback(
    async (run: () => Promise<{ ok: boolean; status: number; type: string | undefined; apply: () => void }>): Promise<WriteResult> => {
      try {
        const r = await run();
        if (r.ok) {
          r.apply();
          return { ok: true };
        }
        if (r.status === 404 || r.status === 409) reload();
        return { ok: false, message: writeMessage(r.status, r.type) };
      } catch {
        return { ok: false, message: "problem.unreachable" };
      }
    },
    [reload],
  );

  const add = useCallback(
    (date: string, recipe: RecipeSummary) =>
      write(async () => {
        const { data, error, response } = await api.POST("/api/households/{householdId}/meal-plans/{weekStart}/entries", {
          params: { path: { householdId, weekStart } },
          body: { date, recipeId: recipe.id, servings: null },
        });
        return {
          ok: !!data,
          status: response.status,
          type: problemTypeOf(error),
          apply: () => {
            if (!data) return;
            setImages((prev) => ({ ...prev, [recipe.id]: { thumbUrl: recipe.thumbUrl ?? null, cardUrl: recipe.cardUrl ?? null } }));
            change((list) => [...list, data]);
          },
        };
      }),
    [change, householdId, weekStart, write],
  );

  const setServings = useCallback(
    (entry: PlanEntry, servings: number) =>
      write(async () => {
        const { data, error, response } = await api.PUT(
          "/api/households/{householdId}/meal-plans/{weekStart}/entries/{entryId}",
          { params: { path: { householdId, weekStart, entryId: entry.id } }, body: { date: entry.date, servings } },
        );
        return {
          ok: !!data,
          status: response.status,
          type: problemTypeOf(error),
          apply: () => data && change((list) => list.map((e) => (e.id === entry.id ? data : e))),
        };
      }),
    [change, householdId, weekStart, write],
  );

  const remove = useCallback(
    (entry: PlanEntry) =>
      write(async () => {
        const { error, response } = await api.DELETE(
          "/api/households/{householdId}/meal-plans/{weekStart}/entries/{entryId}",
          { params: { path: { householdId, weekStart, entryId: entry.id } } },
        );
        return {
          ok: response.ok,
          status: response.status,
          type: problemTypeOf(error),
          apply: () => change((list) => list.filter((e) => e.id !== entry.id)),
        };
      }),
    [change, householdId, weekStart, write],
  );

  /** Adults freeze or free the week (T-026). */
  const setLocked = useCallback(
    (locked: boolean) =>
      write(async () => {
        const params = { params: { path: { householdId, weekStart } } };
        const { error, response } = locked
          ? await api.PUT("/api/households/{householdId}/meal-plans/{weekStart}/lock", params)
          : await api.DELETE("/api/households/{householdId}/meal-plans/{weekStart}/lock", params);
        return {
          ok: response.ok,
          status: response.status,
          type: problemTypeOf(error),
          apply: () =>
            setLoaded((prev) =>
              prev?.key === key && prev.result.status === "ok" ? { key, result: { ...prev.result, locked } } : prev,
            ),
        };
      }),
    [householdId, key, weekStart, write],
  );

  const plan: Plan = !current ? { status: "loading" } : current;
  return { plan, images, add, setServings, remove, reload, setLocked };
}
