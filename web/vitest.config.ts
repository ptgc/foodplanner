import { defineConfig } from "vitest/config";

// Separate from vite.config.ts so tests do not load the React Router build plugin.
export default defineConfig({
  esbuild: { jsx: "automatic" },
  test: {
    environment: "jsdom",
    setupFiles: ["./vitest.setup.ts"],
    include: ["app/**/*.test.{ts,tsx}"],
  },
});
