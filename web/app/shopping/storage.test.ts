import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
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
    writeCachedList("h1", "2026-10-19", list, "v1");
    expect(readCachedList("h1", "2026-10-19")?.version).toBe("v1");
    expect(readCachedList("h2", "2026-10-19")).toBeNull();
    expect(readCachedList("h1", "2026-10-26")).toBeNull();
  });

  it("tolerates write failures (quota, blocked storage)", () => {
    vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      throw new DOMException("full", "QuotaExceededError");
    });
    expect(() => writeCachedList("h1", "2026-10-19", list, "v1")).not.toThrow();
    expect(() => writeQueue([{ householdId: "h1", weekStart: "w", itemId: "i", checked: true }])).not.toThrow();
  });

  it("tolerates read failures and corrupt data", () => {
    window.localStorage.setItem("foodplanner.shopping.list.h1.2026-10-19", "{not json");
    window.localStorage.setItem("foodplanner.shopping.queue", "{not json");
    expect(readCachedList("h1", "2026-10-19")).toBeNull();
    expect(readQueue()).toEqual([]);
    vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => {
      throw new Error("blocked");
    });
    expect(readCachedList("h1", "2026-10-19")).toBeNull();
    expect(readQueue()).toEqual([]);
  });

  it("drops malformed queue entries", () => {
    window.localStorage.setItem(
      "foodplanner.shopping.queue",
      JSON.stringify([{ itemId: 1 }, { householdId: "h", weekStart: "w", itemId: "i", checked: true }]),
    );
    expect(readQueue()).toHaveLength(1);
  });

  it("clears cached lists and the queue", () => {
    writeCachedList("h1", "2026-10-19", list, null);
    writeQueue([{ householdId: "h1", weekStart: "w", itemId: "i", checked: true }]);
    window.localStorage.setItem("other", "keep");
    clearShoppingStorage();
    expect(readCachedList("h1", "2026-10-19")).toBeNull();
    expect(readQueue()).toEqual([]);
    expect(window.localStorage.getItem("other")).toBe("keep");
  });
});
