import createClient from "openapi-fetch";
import type { paths } from "./schema";

// All API types come from the generated schema.d.ts (npm run api:generate); none are hand-written.
export const api = createClient<paths>({
  // Absolute origin so Request URLs resolve in browsers and jsdom; the dev server/nginx proxy /api.
  baseUrl: typeof window === "undefined" ? "" : window.location.origin,
  // Resolve fetch at call time so tests can stub it.
  fetch: (request) => globalThis.fetch(request),
});
