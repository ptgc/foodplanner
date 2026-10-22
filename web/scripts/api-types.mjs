// Generates TypeScript types from the committed OpenAPI document (../openapi/openapi.v1.json).
//   node scripts/api-types.mjs generate   rewrites app/api/schema.d.ts
//   node scripts/api-types.mjs check      regenerates to a temp file and fails if the committed file differs
import { execFileSync } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const webDir = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const input = resolve(webDir, "..", "openapi", "openapi.v1.json");
const committed = resolve(webDir, "app", "api", "schema.d.ts");
const cli = resolve(webDir, "node_modules", "openapi-typescript", "bin", "cli.js");

function generate(output) {
  execFileSync(process.execPath, [cli, input, "-o", output], { stdio: "inherit" });
}

const normalize = (text) => text.replace(/\r\n/g, "\n");
const mode = process.argv[2];

if (mode === "generate") {
  generate(committed);
} else if (mode === "check") {
  const dir = mkdtempSync(join(tmpdir(), "api-types-"));
  try {
    const fresh = join(dir, "schema.d.ts");
    generate(fresh);
    if (normalize(readFileSync(fresh, "utf8")) !== normalize(readFileSync(committed, "utf8"))) {
      console.error("api:check FAILED: app/api/schema.d.ts is out of date. Run `npm run api:generate` and commit it.");
      process.exit(1);
    }
    console.log("api:check ok: app/api/schema.d.ts matches openapi/openapi.v1.json");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
} else {
  console.error("usage: api-types.mjs generate|check");
  process.exit(2);
}
