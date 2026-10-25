import { afterEach, describe, expect, it, vi } from "vitest";
import { clearRuntimeCaches } from "./clear-caches";

function fakeCaches(names: string[]) {
  const store = new Set(names);
  return {
    store,
    keys: vi.fn(async () => [...store]),
    delete: vi.fn(async (name: string) => store.delete(name)),
  };
}

afterEach(() => vi.unstubAllGlobals());

describe("clearRuntimeCaches", () => {
  it("deletes the per-user runtime caches and keeps the precache of the app itself", async () => {
    const fake = fakeCaches(["fp-precache-abc123", "fp-runtime-api", "fp-runtime-images", "other-site-cache"]);
    vi.stubGlobal("caches", fake);
    await clearRuntimeCaches();
    expect([...fake.store].sort()).toEqual(["fp-precache-abc123", "other-site-cache"]);
  });

  it("does nothing without the Cache API", async () => {
    vi.stubGlobal("caches", undefined);
    await expect(clearRuntimeCaches()).resolves.toBeUndefined();
  });

  it("never throws when the Cache API fails", async () => {
    vi.stubGlobal("caches", {
      keys: vi.fn(async () => {
        throw new Error("blocked");
      }),
    });
    await expect(clearRuntimeCaches()).resolves.toBeUndefined();
  });
});
