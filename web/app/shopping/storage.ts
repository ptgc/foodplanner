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

const cacheKey = (householdId: string, weekStart: string) => `${CACHE_PREFIX}${householdId}.${weekStart}`;

// Everything here is best effort: storage can be blocked, full or corrupt, and the page must work without it.

export function readCachedList(householdId: string, weekStart: string): CachedList | null {
  try {
    const raw = window.localStorage.getItem(cacheKey(householdId, weekStart));
    if (!raw) return null;
    const parsed = JSON.parse(raw) as Partial<CachedList> | null;
    if (!parsed?.list || parsed.list.weekStart !== weekStart) return null;
    return { version: parsed.version ?? null, list: parsed.list, savedAt: parsed.savedAt ?? "" };
  } catch {
    return null;
  }
}

export function writeCachedList(householdId: string, weekStart: string, list: ShoppingList, version: string | null) {
  try {
    const value: CachedList = { version, list, savedAt: new Date().toISOString() };
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
