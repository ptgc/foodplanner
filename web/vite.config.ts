import { reactRouter } from "@react-router/dev/vite";
import { defineConfig } from "vite";

// The dev server proxies /api to the API, mirroring the same-origin proxy in web/nginx.conf.
// Override with API_PROXY_TARGET (default: dotnet run --urls http://localhost:5000).
const apiTarget = process.env.API_PROXY_TARGET ?? "http://localhost:5000";

export default defineConfig({
  plugins: [reactRouter()],
  server: {
    port: 5173,
    // Same as web/nginx.conf: the set-password link carries a one-time token (T-072).
    headers: { "Referrer-Policy": "no-referrer" },
    proxy: {
      // Caveat: the hub rejects an Origin whose host differs from Host (403 forbidden-origin), so API_PROXY_TARGET=http://api:8080 with changeOrigin breaks it; the default localhost:5000 is fine.
      // ws: true lets the SignalR hub (/api/hubs/...) upgrade to a WebSocket through the dev proxy.
      "/api": { target: apiTarget, changeOrigin: true, ws: true },
    },
  },
});
