import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  CACHE_SCHEMA,
  clearShoppingStorage,
  readCachedList,
  readQueue,
  writeCachedList,
  writeQueue,
  type ShoppingList,
} from "./storage";

const list: ShoppingList = { id: null, weekStart: "2026-10-19", main: [], checkYouHave: [] };

beforeEach(() => window.localStorage.clear());
afterEach(() => vi.restoreAllMocks());

describe("offline cache", () => {
  it("round-trips a list per household and week", () => {
    writeCachedList("h1", "2026-10-19", list, "v1", "en");
    expect(readCachedList("h1", "2026-10-19", "en")?.version).toBe("v1");
    expect(readCachedList("h2", "2026-10-19", "en")).toBeNull();
    expect(readCachedList("h1", "2026-10-26", "en")).toBeNull();
  });

  it("returns a saved list only in the language it was saved in (T-078)", () => {
    writeCachedList("h1", "2026-10-19", list, "v1", "nb");
    expect(readCachedList("h1", "2026-10-19", "en")).toBeNull();
    expect(readCachedList("h1", "2026-10-19", "nb")?.version).toBe("v1");
    writeCachedList("h1", "2026-10-19", list, "v2", "en"); // the newest list replaces it
    expect(readCachedList("h1", "2026-10-19", "nb")).toBeNull();
    expect(readCachedList("h1", "2026-10-19", "en")?.version).toBe("v2");
  });

  it("tolerates write failures (quota, blocked storage)", () => {
    vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      throw new DOMException("full", "QuotaExceededError");
    });
    expect(() => writeCachedList("h1", "2026-10-19", list, "v1", "en")).not.toThrow();
    expect(() => writeQueue([{ householdId: "h1", weekStart: "w", itemId: "i", checked: true }])).not.toThrow();
  });

  it("tolerates read failures and corrupt data", () => {
    window.localStorage.setItem("foodplanner.shopping.list.h1.2026-10-19", "{not json");
    window.localStorage.setItem("foodplanner.shopping.queue", "{not json");
    expect(readCachedList("h1", "2026-10-19", "en")).toBeNull();
    expect(readQueue()).toEqual([]);
    vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => {
      throw new Error("blocked");
    });
    expect(readCachedList("h1", "2026-10-19", "en")).toBeNull();
    expect(readQueue()).toEqual([]);
  });

  it("drops malformed queue entries", () => {
    window.localStorage.setItem(
      "foodplanner.shopping.queue",
      JSON.stringify([{ itemId: 1 }, { householdId: "h", weekStart: "w", itemId: "i", checked: true }]),
    );
    expect(readQueue()).toHaveLength(1);
  });

  const key = "foodplanner.shopping.list.h1.2026-10-19";
  const item = { id: "i", name: "Melk", isManual: false, section: "Main", storeSection: "Dairy", quantities: [], recipes: [], alternatives: [], checked: false, isWeekly: false, atHome: null };

  it("writes the schema version and reads it back", () => {
    const withItem = { ...list, main: [item] } as unknown as ShoppingList;
    writeCachedList("h1", "2026-10-19", withItem, "v1", "en");
    expect(JSON.parse(window.localStorage.getItem(key)!).schema).toBe(CACHE_SCHEMA);
    expect(readCachedList("h1", "2026-10-19", "en")?.list.main).toHaveLength(1);
  });

  it("drops (and deletes) a list saved without a schema, e.g. by a build before T-076/T-085", () => {
    const old = { id: null, weekStart: "2026-10-19", main: [{ id: "i", name: "Melk", quantities: [], recipes: [], checked: false }], checkYouHave: [] };
    window.localStorage.setItem(key, JSON.stringify({ version: "v0", list: old, savedAt: "x" }));
    expect(readCachedList("h1", "2026-10-19", "en")).toBeNull();
    expect(window.localStorage.getItem(key)).toBeNull();
  });

  it("drops a list of another schema version, and one whose items have an unexpected shape", () => {
    window.localStorage.setItem(key, JSON.stringify({ schema: CACHE_SCHEMA - 1, version: null, list: { ...list, main: [item] }, savedAt: "x" }));
    expect(readCachedList("h1", "2026-10-19", "en")).toBeNull();
    const broken = { ...item, alternatives: undefined };
    window.localStorage.setItem(key, JSON.stringify({ schema: CACHE_SCHEMA, version: null, list: { ...list, main: [broken] }, savedAt: "x" }));
    expect(readCachedList("h1", "2026-10-19", "en")).toBeNull();
    window.localStorage.setItem(key, JSON.stringify({ schema: CACHE_SCHEMA, version: null, list: { weekStart: "2026-10-19" }, savedAt: "x" }));
    expect(readCachedList("h1", "2026-10-19", "en")).toBeNull();
  });

  it("keeps the check-off queue when a saved list is dropped", () => {
    const op = { householdId: "h1", weekStart: "2026-10-19", itemId: "i", checked: true };
    writeQueue([op]);
    window.localStorage.setItem(key, JSON.stringify({ version: "v0", list: { ...list, main: [{ id: "i" }] }, savedAt: "x" }));
    expect(readCachedList("h1", "2026-10-19", "en")).toBeNull();
    expect(readQueue()).toEqual([op]);
  });

  it("clears cached lists and the queue", () => {
    writeCachedList("h1", "2026-10-19", list, null, "en");
    writeQueue([{ householdId: "h1", weekStart: "w", itemId: "i", checked: true }]);
    window.localStorage.setItem("other", "keep");
    clearShoppingStorage();
    expect(readCachedList("h1", "2026-10-19", "en")).toBeNull();
    expect(readQueue()).toEqual([]);
    expect(window.localStorage.getItem("other")).toBe("keep");
  });
});
