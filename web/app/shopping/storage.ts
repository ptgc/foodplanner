import type { components } from "../api/schema";

export type ShoppingList = components["schemas"]["ShoppingListResponse"];

/** A queued check-off. Set semantics (an intended state, not a toggle), so replaying is idempotent. */
export type CheckOp = {
  householdId: string;
  weekStart: string;
  itemId: string;
  checked: boolean;
};

export type CachedList = { version: string | null; list: ShoppingList; savedAt: string };

const CACHE_PREFIX = "foodplanner.shopping.list.";
const QUEUE_KEY = "foodplanner.shopping.queue";

/**
 * Version of the saved-list format. Bump it whenever ShoppingListResponse / ShoppingListItemResponse gain or change a
 * field the page reads (T-076 alternatives and T-085 storeSection were added without it, and an old saved list crashed
 * the page: T-088). A saved list with another version is dropped, never rendered; the fresh fetch replaces it.
 * The check-off queue is deliberately not versioned: its ops are only {household, week, item id, checked}.
 */
export const CACHE_SCHEMA = 2;

const cacheKey = (householdId: string, weekStart: string) => `${CACHE_PREFIX}${householdId}.${weekStart}`;

function isItem(v: unknown): boolean {
  const i = v as Record<string, unknown> | null;
  return (
    !!i &&
    typeof i.id === "string" &&
    typeof i.name === "string" &&
    typeof i.checked === "boolean" &&
    typeof i.storeSection === "string" &&
    Array.isArray(i.quantities) &&
    Array.isArray(i.recipes) &&
    Array.isArray(i.alternatives)
  );
}

/** True when `v` has the shape the page renders. Guards against a saved list of an unknown shape. */
export function isShoppingList(v: unknown): v is ShoppingList {
  const l = v as Record<string, unknown> | null;
  return (
    !!l &&
    typeof l.weekStart === "string" &&
    Array.isArray(l.main) &&
    Array.isArray(l.checkYouHave) &&
    l.main.every(isItem) &&
    l.checkYouHave.every(isItem)
  );
}

function discard(key: string) {
  try {
    window.localStorage.removeItem(key);
  } catch {
    // Best effort.
  }
}

// Everything here is best effort: storage can be blocked, full or corrupt, and the page must work without it.

export function readCachedList(householdId: string, weekStart: string): CachedList | null {
  try {
    const raw = window.localStorage.getItem(cacheKey(householdId, weekStart));
    if (!raw) return null;
    const parsed = JSON.parse(raw) as (Partial<CachedList> & { schema?: unknown }) | null;
    if (!parsed || parsed.schema !== CACHE_SCHEMA || !isShoppingList(parsed.list) || parsed.list.weekStart !== weekStart) {
      discard(cacheKey(householdId, weekStart)); // another app version wrote it, or it is damaged: never render it
      return null;
    }
    return { version: parsed.version ?? null, list: parsed.list, savedAt: parsed.savedAt ?? "" };
  } catch {
    return null;
  }
}

export function writeCachedList(householdId: string, weekStart: string, list: ShoppingList, version: string | null) {
  try {
    const value = { schema: CACHE_SCHEMA, version, list, savedAt: new Date().toISOString() };
    window.localStorage.setItem(cacheKey(householdId, weekStart), JSON.stringify(value));
  } catch {
    // Quota or blocked storage: the live list still works, only offline reload is lost.
  }
}

export function readQueue(): CheckOp[] {
  try {
    const raw = window.localStorage.getItem(QUEUE_KEY);
    const parsed: unknown = raw ? JSON.parse(raw) : [];
    if (!Array.isArray(parsed)) return [];
    return parsed.filter(
      (o): o is CheckOp =>
        !!o &&
        typeof o.householdId === "string" &&
        typeof o.weekStart === "string" &&
        typeof o.itemId === "string" &&
        typeof o.checked === "boolean",
    );
  } catch {
    return [];
  }
}

export function writeQueue(queue: CheckOp[]) {
  try {
    if (queue.length === 0) window.localStorage.removeItem(QUEUE_KEY);
    else window.localStorage.setItem(QUEUE_KEY, JSON.stringify(queue));
  } catch {
    // The queue then lives in memory only and is lost on reload.
  }
}

/** Remove cached lists and the queue (log out: do not leave one user's data for the next). */
export function clearShoppingStorage() {
  try {
    const doomed: string[] = [QUEUE_KEY];
    for (let i = 0; i < window.localStorage.length; i++) {
      const key = window.localStorage.key(i);
      if (key?.startsWith(CACHE_PREFIX)) doomed.push(key);
    }
    doomed.forEach((k) => window.localStorage.removeItem(k));
  } catch {
    // Nothing to clear if storage is unavailable.
  }
}
