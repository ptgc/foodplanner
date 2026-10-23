import createClient, { type Middleware } from "openapi-fetch";
import type { paths } from "./schema";

// All API types come from the generated schema.d.ts (npm run api:generate); none are hand-written.
const options = {
  // Absolute origin so Request URLs resolve in browsers and jsdom; the dev server/nginx proxy /api.
  baseUrl: typeof window === "undefined" ? "" : window.location.origin,
  // Resolve fetch at call time so tests can stub it.
  fetch: (request: Request) => globalThis.fetch(request),
};

// Header name from the T-009 spec: the request token travels in X-XSRF-TOKEN.
export const ANTIFORGERY_HEADER = "X-XSRF-TOKEN";

const safeMethods = new Set(["GET", "HEAD", "OPTIONS", "TRACE"]);

// Separate client without the middleware, so fetching a token never needs a token.
const tokenClient = createClient<paths>(options);

let cachedToken: Promise<string | undefined> | undefined;

async function fetchToken(): Promise<string | undefined> {
  try {
    const { data } = await tokenClient.GET("/api/auth/antiforgery");
    return data?.token;
  } catch {
    return undefined;
  }
}

/** Forget the cached token. Call after login and logout: tokens are bound to the signed-in identity. */
export function resetAntiforgeryToken(): void {
  cachedToken = undefined;
}

const antiforgery: Middleware = {
  async onRequest({ request }) {
    if (safeMethods.has(request.method.toUpperCase())) return undefined;
    cachedToken ??= fetchToken();
    const token = await cachedToken;
    if (token) request.headers.set(ANTIFORGERY_HEADER, token);
    else cachedToken = undefined; // do not cache a failure
    return request;
  },
};

export const api = createClient<paths>(options);
api.use(antiforgery);
