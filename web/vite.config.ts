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
      "/api": { target: apiTarget, changeOrigin: true },
    },
  },
});
