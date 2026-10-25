// Fails if the production JavaScript (build/client/assets/*.js, gzipped) is over the budgets in web/README.md
// "Performance budget". Cheap: reads the files `npm run build` just wrote. `--print` only reports the sizes.
//
// Three numbers (T-041). React Router already splits every route into its own chunk and loads it on navigation, so a
// visit downloads far less than the sum of all chunks:
//   initial     what index.html loads before any route: the router manifest, the entry chunk, the root route and
//               their shared chunks (React, the i18n module, the session).
//   first load  the initial JS plus the chunks of one route (and its parent layouts); reported per route, the budget
//               applies to the heaviest route. This is what a cold visit to that URL downloads.
//   total       every chunk. The service worker precaches all of them in the background (T-041), so this is the
//               one-time cost of "works offline", not of the first paint.
import { readdirSync, readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { gzipSync } from "node:zlib";

const budgetKb = { initial: 140, firstLoad: 170, total: 210 };
const assets = resolve(dirname(fileURLToPath(import.meta.url)), "..", "build", "client", "assets");

const sizes = new Map(); // "/assets/x.js" -> gzipped bytes
for (const file of readdirSync(assets)) {
  if (file.endsWith(".js")) sizes.set(`/assets/${file}`, gzipSync(readFileSync(join(assets, file))).length);
}
const kb = (urls) => [...urls].reduce((sum, url) => sum + (sizes.get(url) ?? 0), 0) / 1024;

const manifestFile = [...sizes.keys()].find((url) => /\/manifest-[0-9a-f]+\.js$/.test(url));
if (!manifestFile) throw new Error("no React Router manifest chunk in build/client/assets");
const source = readFileSync(join(assets, manifestFile.split("/").pop()), "utf8");
const manifest = JSON.parse(source.slice(source.indexOf("{"), source.lastIndexOf("}") + 1));

const chunksOf = (route) => [route.module, ...(route.imports ?? [])];
const initial = new Set([manifestFile, ...chunksOf(manifest.entry), ...chunksOf(manifest.routes.root)]);

const firstLoads = [];
for (const [id, route] of Object.entries(manifest.routes)) {
  if (id === "root") continue;
  const chunks = new Set(initial);
  for (let r = route; r; r = manifest.routes[r.parentId]) chunksOf(r).forEach((url) => chunks.add(url));
  firstLoads.push({ id, kb: kb(chunks) });
}
firstLoads.sort((a, b) => b.kb - a.kb);

const result = { initial: kb(initial), firstLoad: firstLoads[0].kb, total: kb(sizes.keys()) };
console.log(`bundle: initial ${result.initial.toFixed(1)} KB (budget ${budgetKb.initial})`);
console.log(`bundle: first load of the heaviest route ${firstLoads[0].id} ${result.firstLoad.toFixed(1)} KB (budget ${budgetKb.firstLoad})`);
console.log(`bundle: total ${result.total.toFixed(1)} KB gzipped JS over ${sizes.size} chunks (budget ${budgetKb.total})`);
if (process.argv[2] === "--print") {
  for (const { id, kb: size } of firstLoads) console.log(`  first load ${id}: ${size.toFixed(1)} KB`);
  process.exit(0);
}
const over = Object.keys(budgetKb).filter((key) => result[key] > budgetKb[key]);
if (over.length > 0) {
  console.error(`bundle-size FAILED: over budget: ${over.join(", ")}. Trim imports or raise a budget on purpose (README).`);
  process.exit(1);
}
