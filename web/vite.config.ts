import { reactRouter } from "@react-router/dev/vite";
import { defineConfig } from "vite";

// The dev server proxies /api to the API, mirroring the same-origin proxy in web/nginx.conf.
// Override with API_PROXY_TARGET (default: dotnet run --urls http://localhost:5000).
const apiTarget = process.env.API_PROXY_TARGET ?? "http://localhost:5000";

export default defineConfig({
  plugins: [reactRouter()],
  server: {
    port: 5173,
    proxy: {
      // ws: true lets the SignalR hub (/api/hubs/...) upgrade to a WebSocket through the dev proxy.
      "/api": { target: apiTarget, changeOrigin: true, ws: true },
    },
  },
});
