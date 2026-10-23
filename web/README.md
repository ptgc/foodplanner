# web

React Router v7 SPA (ssr: false), TypeScript strict.

- `npm ci` then `npm run dev` serves http://localhost:5173. `/api` is proxied to `API_PROXY_TARGET` (default `http://localhost:5000`).
  Start the API with `dotnet run --project src/Api --urls http://localhost:5000`.
- `npm run build` writes the static SPA to `build/client`. `npm run lint`, `npm run typecheck`, `npm test`.
- `npm run api:generate` regenerates `app/api/schema.d.ts` from `../openapi/openapi.v1.json`; `npm run api:check` fails if it is stale.
  Never hand-edit the generated file; use the `api` client in `app/api/client.ts`.
- The container build (`web/Dockerfile`) runs `npm ci` and `npm run build`, then serves `build/client` with nginx (SPA fallback, `/api` proxied to the api service).
