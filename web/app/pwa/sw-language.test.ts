import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

// Runs the real sw/sw.js against a fake worker scope (T-078): the runtime cache of recipe and tag answers must never
// return an answer written in the other language, however the network behaves.
type FetchEvent = { request: Request; respondWith: (r: Promise<Response>) => void };

function setup(network: (request: Request) => Promise<Response>) {
  const source = readFileSync("sw/sw.js", "utf8").replace("/*BUILD_ID*/", "t").replace("/*PRECACHE_URLS*/ []", "[]");
  const stores = new Map<string, Map<string, Response>>();
  const store = (name: string) => stores.get(name) ?? stores.set(name, new Map()).get(name)!;
  const fakeCaches = {
    keys: async () => [...stores.keys()],
    delete: async (name: string) => stores.delete(name),
    open: async (name: string) => {
      const entries = store(name);
      return {
        addAll: async () => undefined,
        match: async (r: Request) => entries.get(r.url)?.clone(),
        put: async (r: Request, response: Response) => void entries.set(r.url, response),
        keys: async () => [...entries.keys()].map((url) => new Request(url)),
        delete: async (r: Request) => entries.delete(r.url),
      };
    },
  };
  const listeners = new Map<string, (event: FetchEvent) => void>();
  const scope = {
    addEventListener: (type: string, fn: (event: FetchEvent) => void) => listeners.set(type, fn),
    location: { origin: "http://localhost" },
    clients: { claim: async () => undefined },
  };
  new Function("self", "caches", "fetch", source)(scope, fakeCaches, network);

  const get = async (path: string, language: string | null): Promise<string> => {
    const headers: Record<string, string> = language ? { "Accept-Language": language } : {};
    let answered: Promise<Response> | undefined;
    listeners.get("fetch")!({
      request: new Request(`http://localhost${path}`, { headers }),
      respondWith: (r) => (answered = r),
    });
    return (await answered!).text();
  };
  return { get, stores };
}

const recipe = "/api/households/h1/recipes/r1";

describe("service worker API cache and language", () => {
  it("serves the cached answer of the same language when the network is down, never the other language's", async () => {
    let online = true;
    const { get } = setup(async (request) => {
      if (!online) throw new TypeError("offline");
      return new Response(`recipe in ${request.headers.get("Accept-Language")}`);
    });

    expect(await get(recipe, "en")).toBe("recipe in en");
    expect(await get(recipe, "nb")).toBe("recipe in nb");
    online = false;
    expect(await get(recipe, "en")).toBe("recipe in en");
    expect(await get(recipe, "nb")).toBe("recipe in nb");
  });

  it("has nothing to offer for a language that was never fetched (offline): the request fails instead of answering in the other language", async () => {
    let online = true;
    const { get } = setup(async (request) => {
      if (!online) throw new TypeError("offline");
      return new Response(`tags in ${request.headers.get("Accept-Language")}`);
    });

    expect(await get("/api/households/h1/tags", "en")).toBe("tags in en");
    online = false;
    await expect(get("/api/households/h1/tags", "nb")).rejects.toThrow("offline");
    await expect(get("/api/households/h1/tags", null)).rejects.toThrow("offline"); // no language: matches none
  });

  it("keeps one entry per language and url (the list url with its query too)", async () => {
    const { get, stores } = setup(async (request) => new Response(request.headers.get("Accept-Language") ?? ""));

    await get("/api/households/h1/recipes?tag=a&limit=12", "en");
    await get("/api/households/h1/recipes?tag=a&limit=12", "nb");
    await get("/api/households/h1/recipes?tag=a&limit=12", "nb");

    expect([...stores.get("fp-runtime-api")!.keys()].sort()).toEqual([
      "http://localhost/api/households/h1/recipes?tag=a&limit=12&__lang=en",
      "http://localhost/api/households/h1/recipes?tag=a&limit=12&__lang=nb",
    ]);
  });

  it("does not put images in the language-keyed cache (their urls are immutable and language-free)", async () => {
    const { get, stores } = setup(async () => new Response("png"));

    await get("/api/households/h1/recipes/r1/image/v1/card", "nb");

    expect([...stores.get("fp-runtime-images")!.keys()]).toEqual(["http://localhost/api/households/h1/recipes/r1/image/v1/card"]);
  });
});
