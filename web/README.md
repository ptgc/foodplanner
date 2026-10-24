# web

React Router v7 SPA (ssr: false), TypeScript strict.

- `npm ci` then `npm run dev` serves http://localhost:5173. `/api` is proxied to `API_PROXY_TARGET` (default `http://localhost:5000`).
  Start the API with `dotnet run --project src/Api --urls http://localhost:5000`.
- `npm run build` writes the static SPA to `build/client`. `npm run lint`, `npm run typecheck`, `npm test`.
- `npm run api:generate` regenerates `app/api/schema.d.ts` from `../openapi/openapi.v1.json`; `npm run api:check` fails if it is stale.
  Never hand-edit the generated file; use the `api` client in `app/api/client.ts`.
- The container build (`web/Dockerfile`) runs `npm ci` and `npm run build`, then serves `build/client` with nginx (SPA fallback, `/api` proxied to the api service).

## Shopping list screen (T-036, `/shopping`)
- Code: `app/routes/shopping.tsx` (UI, all text in `app/shopping/strings.ts`, English until Q-013), `app/shopping/use-shopping-list.ts` (state), `realtime.ts` (SignalR client following the contract in docs/domain.md "Shopping list sync"), `queue.ts` (offline check-off queue), `storage.ts` (localStorage cache and queue, every access in try/catch).
- Week is in the URL (`?week=yyyy-MM-dd`, a Monday; default the current ISO week). Writes go through REST; the hub only signals changes and serves `GetState`.
- Offline: the list of each household and week is cached in localStorage; check-offs are queued as intended states (`checked: true|false`, idempotent), one per item (latest wins), persisted, shown optimistically, replayed in order when the API answers again (online event, reconnect, or a 5 s retry), then the list is re-read. The last confirmed session (user and households) is also kept so a reload while the API is down can render the cached list; it is cleared on log out, together with cached lists and the queue.
- Limit: there is no service worker (PWA is T-041). Offline means "the page is open, or the app is served but the API is unreachable". A browser that is fully offline cannot load the page itself. Generate, add and remove need the API and are disabled offline.
