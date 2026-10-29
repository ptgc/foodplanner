import type { APIRequestContext } from "@playwright/test";

/**
 * Finds and removes the data e2e specs create (T-083). Specs clean up after themselves; this is the safety net and the
 * audit: after a run, `sweep` lists what still carries the run's tag, deletes it, and reports it so the run fails.
 *
 * Only a matcher decides what is test data. Anything it does not match (the architect's real recipes, ingredients and
 * households) is never deleted.
 */
export type Matcher = (text: string | null | undefined) => boolean;

export type Leftover = { kind: string; name: string; id: string; where: string; removed: boolean; note?: string };

/** Names that carry this run's tag. The tag is letters only, so it survives the digit-to-letter mapping of invented names. */
export function runMatcher(tag: string): Matcher {
  const needle = tag.toLowerCase();
  return (text) => !!text && text.toLowerCase().includes(needle);
}

/**
 * Names that earlier runs (before the run tag existed) left behind. Deliberately narrow: each pattern is something a
 * spec generated, built from the 13-digit Date.now() stamp, the invented Zqx word, or a known "E2E ..." title.
 */
export const legacyMatcher: Matcher = (text) =>
  !!text &&
  (/\b\d{13}-\d+-\d+\b/.test(text) ||
    /^Zqx[a-j]{10,}$/.test(text) ||
    /^(0 )?E2E (Import|Draft|Form|Book|Photo size|Kinds|Merge|Sections|Sprak|Pannekaker|Pasta|plan|melk|ost|live|offline|gammel|kopi|ferdig|del|sw)\b/.test(text) ||
    /^Zz E2E\b/.test(text));

export type SweepOptions = {
  request: APIRequestContext;
  /** What counts as test data. */
  matches: Matcher;
  /** List only; delete nothing. */
  dryRun: boolean;
  /** Mondays (yyyy-mm-dd) in which specs planned meals or added shopping items. */
  weeks: string[];
};

async function csrf(request: APIRequestContext): Promise<Record<string, string>> {
  const res = await request.get("/api/auth/antiforgery");
  return { "X-XSRF-TOKEN": ((await res.json()) as { token: string }).token };
}

async function json<T>(request: APIRequestContext, url: string): Promise<T> {
  const res = await request.get(url);
  if (!res.ok()) throw new Error(`GET ${url} answered ${res.status()}`);
  return (await res.json()) as T;
}

type RecipeRow = { id: string; title: string };

async function allRecipes(request: APIRequestContext, h: string): Promise<RecipeRow[]> {
  const rows: RecipeRow[] = [];
  for (;;) {
    const page = await json<{ items: RecipeRow[]; total: number }>(request, `/api/households/${h}/recipes?limit=100&offset=${rows.length}`);
    rows.push(...page.items);
    if (page.items.length === 0 || rows.length >= page.total) return rows;
  }
}

export async function sweep(options: SweepOptions): Promise<Leftover[]> {
  const { request, matches, dryRun, weeks } = options;
  const found: Leftover[] = [];

  const remove = async (url: string, leftover: Omit<Leftover, "removed" | "note">) => {
    if (dryRun) {
      found.push({ ...leftover, removed: false, note: "dry run" });
      return;
    }
    const res = await request.delete(url, { headers: await csrf(request) });
    const ok = res.ok() || res.status() === 404;
    found.push({ ...leftover, removed: ok, note: ok ? undefined : `DELETE answered ${res.status()}` });
  };

  const households = await json<{ id: string; name: string }[]>(request, "/api/households");
  for (const household of households) {
    const h = household.id;
    const where = household.name;
    if (matches(household.name)) continue; // the household and everything in it go below

    // 1. Planned meals and manual shopping items in the weeks specs used.
    const recipes = await allRecipes(request, h);
    const testRecipeIds = new Set(recipes.filter((r) => matches(r.title)).map((r) => r.id));
    for (const week of weeks) {
      const plan = await json<{ entries: { id: string; recipeId: string; recipeTitle: string }[] }>(request, `/api/households/${h}/meal-plans/${week}`);
      for (const e of plan.entries.filter((x) => testRecipeIds.has(x.recipeId) || matches(x.recipeTitle))) {
        await remove(`/api/households/${h}/meal-plans/${week}/entries/${e.id}`, { kind: "meal plan entry", name: e.recipeTitle, id: e.id, where: `${where} ${week}` });
      }
      const list = await json<{
        main: { id: string; name: string; isManual: boolean; isWeekly?: boolean }[];
        checkYouHave: { id: string; name: string; isManual: boolean; isWeekly?: boolean }[];
      }>(request, `/api/households/${h}/shopping-lists/${week}`);
      // Manual items, and items the weekly groceries put on the list (T-092; removing one drops its weekly part).
      for (const item of [...list.main, ...list.checkYouHave].filter((x) => (x.isManual || x.isWeekly) && matches(x.name))) {
        await remove(`/api/households/${h}/shopping-lists/${week}/items/${item.id}`, {
          kind: item.isManual ? "manual shopping item" : "weekly shopping item",
          name: item.name,
          id: item.id,
          where: `${where} ${week}`,
        });
      }
      // Regenerating drops the generated lines of recipes that left the plan (only when something was removed here).
      if (!dryRun && found.some((f) => f.where.endsWith(week))) {
        await request.post(`/api/households/${h}/shopping-lists/${week}/generate`, { headers: await csrf(request) });
      }
    }

    // 2. Import drafts.
    const drafts = await json<{ id: string; title: string | null }[]>(request, `/api/households/${h}/imports`);
    for (const d of drafts.filter((x) => matches(x.title))) {
      await remove(`/api/households/${h}/imports/${d.id}`, { kind: "import draft", name: d.title ?? "", id: d.id, where });
    }

    // 3. Recipes (their uploaded images go with them).
    for (const r of recipes.filter((x) => matches(x.title))) {
      await remove(`/api/households/${h}/recipes/${r.id}`, { kind: "recipe", name: r.title, id: r.id, where });
    }

    // 4. Pantry entries and own ingredients (an ingredient in use cannot be deleted, so these come last).
    const pantry = await json<{ ingredientId: string; name: string }[]>(request, `/api/households/${h}/pantry`);
    for (const p of pantry.filter((x) => matches(x.name))) {
      await remove(`/api/households/${h}/pantry/${p.ingredientId}`, { kind: "pantry entry", name: p.name, id: p.ingredientId, where });
    }
    const weeklyGroceries = await json<{ id: string; name: string }[]>(request, `/api/households/${h}/weekly-groceries`);
    for (const w of weeklyGroceries.filter((x) => matches(x.name))) {
      await remove(`/api/households/${h}/weekly-groceries/${w.id}`, { kind: "weekly grocery", name: w.name, id: w.id, where });
    }
    const own = await json<{ id: string; name: string }[]>(request, `/api/households/${h}/ingredients/own`);
    for (const i of own.filter((x) => matches(x.name))) {
      await remove(`/api/households/${h}/ingredients/${i.id}`, { kind: "own ingredient", name: i.name, id: i.id, where });
    }
  }

  // 5. Households created by a spec, with everything in them.
  for (const household of households.filter((x) => matches(x.name))) {
    await remove(`/api/households/${household.id}`, { kind: "household", name: household.name, id: household.id, where: "" });
  }
  return found;
}

export function formatLeftovers(found: Leftover[]): string {
  if (found.length === 0) return "no leftover e2e data";
  return found
    .map((l) => `  ${l.removed ? "removed" : "LEFT   "} ${l.kind}: ${l.name} (${l.id}${l.where ? `, ${l.where}` : ""})${l.note ? ` [${l.note}]` : ""}`)
    .join("\n");
}
