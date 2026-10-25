// Runs after `react-router build` (see package.json "build"): writes build/client/sw.js from sw/sw.js with the list of
// files to precache (the SPA shell, every hashed chunk and stylesheet, icons, manifest, offline page) and a build id
// that changes whenever any of them does, so the browser sees a new service worker exactly when the app changed.
import { createHash } from "node:crypto";
import { readdirSync, readFileSync, statSync, writeFileSync } from "node:fs";
import { dirname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const client = join(root, "build", "client");

function walk(dir) {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name);
    return statSync(path).isDirectory() ? walk(path) : [path];
  });
}

const files = walk(client)
  .map((path) => "/" + relative(client, path).split("\\").join("/"))
  .filter((url) => url !== "/sw.js" && !url.endsWith(".map"))
  .sort();

const hash = createHash("sha256");
hash.update(readFileSync(join(root, "sw", "sw.js")));
for (const url of files) {
  hash.update(url);
  hash.update(readFileSync(join(client, url)));
}
const buildId = hash.digest("hex").slice(0, 12);

const source = readFileSync(join(root, "sw", "sw.js"), "utf8");
const output = source
  .replace("/*PRECACHE_URLS*/ []", JSON.stringify(files))
  .replace("/*BUILD_ID*/", buildId);
if (output === source) throw new Error("sw/sw.js has no placeholders to fill");
writeFileSync(join(client, "sw.js"), output);
console.log(`sw.js: precaching ${files.length} files, build ${buildId}`);
