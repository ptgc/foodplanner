import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

// Runs the real sw/sw.js against a fake worker scope, to check what its activate handler deletes (T-088).
describe("service worker activate", () => {
  it("drops the recipe API cache and old precaches of other builds, keeps images and the current precache", async () => {
    const source = readFileSync("sw/sw.js", "utf8").replace("/*BUILD_ID*/", "new").replace("/*PRECACHE_URLS*/ []", "[]");
    const names = new Set(["fp-precache-old", "fp-precache-new", "fp-runtime-api", "fp-runtime-images"]);
    const listeners = new Map<string, (event: { waitUntil: (p: Promise<unknown>) => void }) => void>();
    const fakeCaches = {
      keys: async () => [...names],
      delete: async (name: string) => names.delete(name),
      open: async () => ({ addAll: async () => undefined }),
    };
    const scope = {
      addEventListener: (type: string, fn: (event: { waitUntil: (p: Promise<unknown>) => void }) => void) => listeners.set(type, fn),
      clients: { claim: async () => undefined },
      location: { origin: "http://localhost" },
    };
    new Function("self", "caches", source)(scope, fakeCaches);

    let done: Promise<unknown> = Promise.resolve();
    listeners.get("activate")!({ waitUntil: (p) => (done = p) });
    await done;

    expect([...names].sort()).toEqual(["fp-precache-new", "fp-runtime-images"]);
  });
});
