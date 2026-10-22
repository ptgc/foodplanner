import type { Config } from "@react-router/dev/config";

// ADR 0001: SPA mode. The build emits static files (build/client) only.
export default {
  ssr: false,
} satisfies Config;
