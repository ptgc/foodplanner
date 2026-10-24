// Fails if the production JavaScript (build/client/assets/*.js, gzipped) is over the budget in web/README.md
// "Performance budget". Cheap: reads the files `npm run build` just wrote. `--print` only reports the size.
import { readdirSync, readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { gzipSync } from "node:zlib";

const budgetKb = 170; // gzipped, all JS of the SPA (entry, routes, vendor)
const assets = resolve(dirname(fileURLToPath(import.meta.url)), "..", "build", "client", "assets");

let bytes = 0;
for (const file of readdirSync(assets)) {
  if (file.endsWith(".js")) bytes += gzipSync(readFileSync(join(assets, file))).length;
}
const kb = bytes / 1024;
console.log(`bundle: ${kb.toFixed(1)} KB gzipped JS (budget ${budgetKb} KB)`);
if (process.argv[2] !== "--print" && kb > budgetKb) {
  console.error(`bundle-size FAILED: ${kb.toFixed(1)} KB > ${budgetKb} KB. Trim imports or raise the budget on purpose (README).`);
  process.exit(1);
}
