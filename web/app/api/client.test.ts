import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { api, resetAntiforgeryToken } from "./client";

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });

let tokenCounter = 0;

function stub() {
  const calls: Request[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (request: Request) => {
      calls.push(request);
      const path = new URL(request.url).pathname;
      if (path === "/api/auth/antiforgery") return json({ token: `token-${++tokenCounter}` });
      return new Response(null, { status: 204 });
    }),
  );
  return calls;
}

beforeEach(() => {
  tokenCounter = 0;
  resetAntiforgeryToken();
});
afterEach(() => vi.unstubAllGlobals());

describe("antiforgery middleware", () => {
  it("does not fetch a token or add the header for GET", async () => {
    const calls = stub();
    await api.GET("/api/auth/me");
    expect(calls).toHaveLength(1);
    expect(calls[0]!.headers.get("X-XSRF-TOKEN")).toBeNull();
  });

  it("sends X-XSRF-TOKEN on unsafe requests and reuses the token", async () => {
    const calls = stub();
    await api.POST("/api/auth/login", { body: { email: "a@b.c", password: "x" } });
    await api.POST("/api/auth/logout");
    const unsafe = calls.filter((c) => c.method === "POST");
    expect(unsafe).toHaveLength(2);
    expect(unsafe.map((c) => c.headers.get("X-XSRF-TOKEN"))).toEqual(["token-1", "token-1"]);
    expect(calls.filter((c) => new URL(c.url).pathname === "/api/auth/antiforgery")).toHaveLength(1);
  });

  it("fetches a fresh token after resetAntiforgeryToken (login/logout)", async () => {
    const calls = stub();
    await api.POST("/api/auth/login", { body: { email: "a@b.c", password: "x" } });
    resetAntiforgeryToken();
    await api.POST("/api/auth/logout");
    const unsafe = calls.filter((c) => c.method === "POST");
    expect(unsafe.map((c) => c.headers.get("X-XSRF-TOKEN"))).toEqual(["token-1", "token-2"]);
  });
});
