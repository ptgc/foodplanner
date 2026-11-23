// Scores the "recipe from a photo" skill (T-118) on a local example set. NOT part of verify.sh or CI: it calls the real Anthropic API
// and spends money, so it only runs by hand with a key in the environment.
//
//   ANTHROPIC_API_KEY=... node scripts/recipe-photo-eval.mjs --book kaptein-sabeltann --model claude-sonnet-5-5 [--effort medium] [--only 1,5] [--dry-run]
//
// Options
//   --book <name>        folder under <dir>/recipe-photos (required)
//   --model <id>         default claude-sonnet-5-5          --effort <low|medium|high>   default medium
//   --only 1,5           only these recipe numbers        --max-tokens <n>             default 12000
//   --dry-run            no network: load the set, build the requests (sizes only) and score a stored answers file
//   --answers <file>     stored answers for --dry-run (default <dir>/recipe-photos/<book>/answers.json; written by every real run)
//   --dir <path>         the eval-private folder (default <repo>/eval-private; it is gitignored: copyrighted pages)
//   --skill-file <path>  score another skill text (for example an older version from git) instead of Skills/recipe-from-photo.md
//   --report-dir <path>  where the JSON report goes (default <dir>/reports)      --no-report   do not write the report
//
// It refuses to run when CI is set, when --book has no folder, or (without --dry-run) when ANTHROPIC_API_KEY is not in the
// environment. The key is never read from a file. It builds the same request as the app: the skill file as `system`, the photos as
// base64 image blocks in order (each preceded by "Photo N"), then the unit codes, and the same output_config (effort + json_schema).
//
// Example set format: <book>/NN-page.jpg (several photos of one recipe: NN-page.jpg, NN-page-2.jpg, ...) and <book>/expected.mjs with
//   export const source = "Book title (publisher)";
//   export const recipes = [{ n, title, servings, cook, tags, tip, lines: [[ingredient, quantity|null, unit|null, note|null, opts]], steps, picture?, crop? }]
//   export const canonical = { "småpoteter": "Potet" }   // optional: ingredient names normalised before comparing
//   export const bookName = "Book title"                  // optional: the name the user would type (default: the part of `source` before "(")
// A null `cook` or `servings` means "the book prints none": that field is not scored (null score, left out of the means).
// Quantity: a printed range is answered as quantityLow..quantity, so an expected value anywhere inside that range counts as right.
// opts: k = "AsNeeded" | "ToTaste" (default Measured when a quantity is given, else ToTaste), p = purpose (default Main), o = optional.
// picture = { photo, box: {x, y, w, h} } or crop = [x0, y0, x1, y1, rotation] (on the EXIF-oriented photo) is scored as IoU >= 0.6.
import { existsSync, readFileSync, readdirSync, writeFileSync, mkdirSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const skillDir = join(root, "src", "Infrastructure", "Import", "Ai", "Skills");

// Names the architect used when the example sets were entered; merged with each set's own `canonical`.
export const DEFAULT_CANONICAL = {
  "småpoteter": "Potet",
  "pepper": "Kvernet pepper",
  "issalat": "Salat",
  "hvetetortilla": "Tortillalefser",
};

// $ per million tokens (input, output).
const PRICES = { opus: [4, 20], sonnet: [2, 10], haiku: [0.1, 0.5] };

function fail(message) {
  console.error(`recipe-photo-eval: ${message}`);
  process.exit(2);
}

export function parseArgs(argv) {
  const args = { model: "claude-sonnet-5-5", effort: "medium", maxTokens: 12000, dryRun: false, report: true };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    const next = () => argv[++i] ?? fail(`${a} needs a value`);
    switch (a) {
      case "--book": args.book = next(); break;
      case "--model": args.model = next(); break;
      case "--effort": args.effort = next(); break;
      case "--only": args.only = next().split(",").map((s) => Number(s.trim())).filter(Number.isFinite); break;
      case "--max-tokens": args.maxTokens = Number(next()); break;
      case "--dry-run": args.dryRun = true; break;
      case "--answers": args.answers = next(); break;
      case "--dir": args.dir = next(); break;
      case "--report-dir": args.reportDir = next(); break;
      case "--skill-file": args.skillFile = next(); break;
      case "--no-report": args.report = false; break;
      default: fail(`unknown option ${a}`);
    }
  }
  return args;
}

// ---- normalising and scoring (pure, exported for the tests) -------------------------------------------------------------

export function fold(text) {
  return String(text ?? "")
    .toLowerCase()
    .replace(/æ/g, "ae").replace(/ø/g, "o").replace(/å/g, "a")
    .normalize("NFD").replace(/[̀-ͯ]/g, "")
    .replace(/[^a-z0-9 ]+/g, " ").replace(/\s+/g, " ").trim();
}

function stem(word) {
  if (word.length > 5 && word.endsWith("ene")) return word.slice(0, -3);
  if (word.length > 4 && (word.endsWith("er") || word.endsWith("en") || word.endsWith("et"))) return word.slice(0, -2);
  if (word.length > 3 && word.endsWith("e")) return word.slice(0, -1);
  if (word.length > 3 && word.endsWith("s")) return word.slice(0, -1);
  return word;
}

export function normName(name, canonical) {
  const mapped = canonical[fold(name)] ?? name;
  return fold(mapped).split(" ").filter(Boolean).map(stem).join(" ");
}

const tokens = (text) => new Set(fold(text).split(" ").filter(Boolean));

/** Share of the expected tokens found in the actual text. */
export function overlap(expected, actual) {
  const e = tokens(expected);
  if (e.size === 0) return 1;
  const a = tokens(actual);
  let hit = 0;
  for (const t of e) if (a.has(t)) hit++;
  return hit / e.size;
}

export function expectedLine([ingredient, quantity, unit, note, opts = {}]) {
  return {
    ingredient,
    quantity: quantity ?? null,
    unit: unit ?? null,
    note: note ?? null,
    amountKind: opts.k ?? (quantity != null ? "Measured" : "ToTaste"),
    purpose: opts.p ?? "Main",
    optional: opts.o === true,
  };
}

export function iou(a, b) {
  const x1 = Math.max(a.x, b.x), y1 = Math.max(a.y, b.y);
  const x2 = Math.min(a.x + a.w, b.x + b.w), y2 = Math.min(a.y + a.h, b.y + b.h);
  const inter = Math.max(0, x2 - x1) * Math.max(0, y2 - y1);
  const union = a.w * a.h + b.w * b.h - inter;
  return union <= 0 ? 0 : inter / union;
}

export const FIELDS = ["title", "servings", "cook", "description", "source", "recall", "precision", "quantity", "unit", "amountKind", "purpose", "optional", "steps", "picture"];

/** Scores one answer against one expected recipe. Every score is 0..1 or null (not applicable); `note` and `pictureFound` are only reported. */
export function scoreRecipe(expected, answer, source, canonical) {
  const lines = (answer.ingredients ?? []).map((i) => ({
    ingredient: i.ingredient, quantity: i.quantity ?? null, quantityLow: i.quantityLow ?? null, unit: i.unit ?? null, note: i.note ?? null,
    amountKind: i.amountKind, purpose: i.purpose, optional: i.isOptional === true,
  }));
  const wanted = expected.lines.map(expectedLine);

  // Pair lines by normalised name, in order, each actual line used once.
  const used = new Set();
  const pairs = [];
  for (const w of wanted) {
    const key = normName(w.ingredient, canonical);
    const at = lines.findIndex((l, i) => !used.has(i) && normName(l.ingredient, canonical) === key);
    if (at >= 0) {
      used.add(at);
      pairs.push([w, lines[at]]);
    }
  }

  const share = (test) => (pairs.length === 0 ? (wanted.length === 0 ? 1 : 0) : pairs.filter(([w, a]) => test(w, a)).length / pairs.length);
  const steps = answer.steps ?? [];
  const stepScore = expected.steps.length === 0 ? 1 : expected.steps.filter((s, i) => i < steps.length && overlap(s, steps[i]) >= 0.7).length / expected.steps.length;

  const bookTitle = fold(String(source).split(/ [–-] |\(/)[0]);
  const box = answer.picture?.box;
  let picture = null;
  const expectedBox = expected.picture?.box ?? (expected.crop ? { x: expected.crop[0], y: expected.crop[1], w: expected.crop[2] - expected.crop[0], h: expected.crop[3] - expected.crop[1] } : null);
  if (expectedBox) picture = box ? (iou(expectedBox, box) >= 0.6 ? 1 : 0) : 0;

  return {
    title: fold(answer.title) === fold(expected.title) ? 1 : 0,
    servings: expected.servings == null ? null : (answer.servings ?? null) === expected.servings ? 1 : 0,
    cook: expected.cook == null ? null : (answer.cookMinutes ?? null) === expected.cook ? 1 : 0,
    description: expected.tip ? (overlap(expected.tip, answer.description ?? "") >= 0.8 ? 1 : 0) : (answer.description ? 0 : 1),
    source: bookTitle && fold(answer.sourceBook ?? "").includes(bookTitle) ? 1 : 0,
    recall: wanted.length === 0 ? 1 : pairs.length / wanted.length,
    precision: lines.length === 0 ? (wanted.length === 0 ? 1 : 0) : pairs.length / lines.length,
    quantity: share((w, a) => (w.quantity == null ? a.quantity == null : a.quantity != null && (Math.abs(a.quantity - w.quantity) <= 0.0001
      || (a.quantityLow != null && w.quantity >= a.quantityLow - 0.0001 && w.quantity <= a.quantity + 0.0001)))),
    unit: share((w, a) => w.unit === a.unit),
    amountKind: share((w, a) => w.amountKind === a.amountKind),
    purpose: share((w, a) => w.purpose === a.purpose),
    optional: share((w, a) => w.optional === a.optional),
    steps: stepScore,
    picture,
    // reported only
    note: share((w, a) => !w.note || fold(a.note ?? "").includes(fold(w.note)) || fold(w.note).includes(fold(a.note ?? "x"))),
    pictureFound: answer.picture ? 1 : 0,
  };
}

export function price(model) {
  const key = Object.keys(PRICES).find((k) => model.includes(k));
  return key ? PRICES[key] : [0, 0];
}

export function cost(model, usage) {
  const [pin, pout] = price(model);
  return ((usage?.input_tokens ?? 0) * pin + (usage?.output_tokens ?? 0) * pout) / 1_000_000;
}

const mean = (xs) => { const v = xs.filter((x) => x != null); return v.length ? v.reduce((a, b) => a + b, 0) / v.length : null; };
const pct = (x) => (x == null ? "  - " : `${Math.round(x * 100)}`.padStart(3) + "%");

export function formatTable(rows) {
  const head = ["n".padEnd(3), ...FIELDS.map((f) => f.padStart(11))].join(" ");
  const body = rows.map((r) => [String(r.n).padEnd(3), ...FIELDS.map((f) => pct(r.scores[f]).padStart(11))].join(" "));
  const means = Object.fromEntries(FIELDS.map((f) => [f, mean(rows.map((r) => r.scores[f]))]));
  const foot = ["all".padEnd(3), ...FIELDS.map((f) => pct(means[f]).padStart(11))].join(" ");
  const overall = mean(Object.values(means));
  return { text: [head, ...body, foot, `overall mean: ${pct(overall).trim()}`].join("\n"), means, overall };
}

// ---- loading the set and building the request ----------------------------------------------------------------------------

function loadSkill(file) {
  const text = readFileSync(file ?? join(skillDir, "recipe-from-photo.md"), "utf8").replace(/\r\n/g, "\n");
  const version = Number(/^Version:\s*(\d+)/.exec(text)?.[1]);
  if (!Number.isInteger(version)) fail("the skill file must start with 'Version: N'");
  const schema = JSON.parse(readFileSync(join(skillDir, "recipe-from-photo.schema.json"), "utf8"));
  return { text, version, schema };
}

function unitCodes() {
  const text = readFileSync(join(root, "src", "Domain", "Catalog", "UnitSeed.cs"), "utf8");
  return [...text.matchAll(/new\("([a-z]+)",/g)].map((m) => m[1]).sort();
}

function photosOf(folder, n) {
  const nn = String(n).padStart(2, "0");
  return readdirSync(folder).filter((f) => new RegExp(`^${nn}-page(-\\d+)?\\.(jpe?g|png|webp)$`, "i").test(f)).sort();
}

export function buildRequest({ model, effort, maxTokens, skill, units, photos, bookName }) {
  const content = [];
  photos.forEach((p, i) => {
    content.push({ type: "text", text: `Photo ${i + 1}` });
    content.push({ type: "image", source: { type: "base64", media_type: p.mediaType, data: p.data } });
  });
  content.push({ type: "text", text: `Unit codes: ${units.join(", ")}.${bookName ? ` Book name typed by the user: ${bookName}` : ""}` });
  return {
    model,
    max_tokens: maxTokens,
    system: skill.text,
    messages: [{ role: "user", content }],
    output_config: { effort, format: { type: "json_schema", schema: skill.schema } },
  };
}

const mediaType = (file) => (/\.png$/i.test(file) ? "image/png" : /\.webp$/i.test(file) ? "image/webp" : "image/jpeg");

async function callApi(body, key) {
  const started = Date.now();
  const response = await fetch("https://api.anthropic.com/v1/messages", {
    method: "POST",
    redirect: "error",
    headers: { "x-api-key": key, "anthropic-version": "2023-06-01", "content-type": "application/json" },
    body: JSON.stringify(body),
  });
  const durationMs = Date.now() - started;
  if (!response.ok) return { error: `HTTP ${response.status}`, durationMs };
  const message = await response.json();
  if (message.stop_reason !== "end_turn") return { error: `stop_reason ${message.stop_reason}`, usage: message.usage, durationMs };
  const text = message.content?.find((b) => b.type === "text")?.text;
  try {
    return { recipe: JSON.parse(text), usage: message.usage, durationMs };
  } catch {
    return { error: "answer is not JSON", usage: message.usage, durationMs };
  }
}

// ---- main ----------------------------------------------------------------------------------------------------------------

export async function main(argv, env = process.env) {
  const args = parseArgs(argv);
  if (env.CI) fail("refusing to run when CI is set: this script calls the real API and costs money.");
  if (!args.book) fail("--book <name> is required.");
  const dir = resolve(args.dir ?? join(root, "eval-private"));
  const folder = join(dir, "recipe-photos", args.book);
  if (!existsSync(folder)) fail(`no example set at ${folder}`);
  const key = env.ANTHROPIC_API_KEY;
  if (!args.dryRun && !key) fail("ANTHROPIC_API_KEY is not set in the environment (it is never read from a file). Use --dry-run to score stored answers without the network.");

  const expectedModule = await import(pathToFileURL(join(folder, "expected.mjs")).href);
  const canonical = Object.fromEntries(Object.entries({ ...DEFAULT_CANONICAL, ...(expectedModule.canonical ?? {}) }).map(([k, v]) => [fold(k), v]));
  const skill = loadSkill(args.skillFile ? resolve(args.skillFile) : null);
  const units = unitCodes();
  const recipes = expectedModule.recipes.filter((r) => !args.only || args.only.includes(r.n));
  if (recipes.length === 0) fail("no recipe selected.");

  const answersFile = resolve(args.answers ?? join(folder, "answers.json"));
  const stored = args.dryRun && existsSync(answersFile) ? JSON.parse(readFileSync(answersFile, "utf8")) : {};
  console.log(`book ${args.book} | model ${args.model} | effort ${args.effort} | skill v${skill.version} | ${recipes.length} recipes${args.dryRun ? " | DRY RUN (no network)" : ""}`);

  const rows = [];
  const answers = {};
  let totalCost = 0;
  for (const recipe of recipes) {
    const files = photosOf(folder, recipe.n);
    if (files.length === 0) { console.warn(`recipe ${recipe.n}: no photo found, skipped`); continue; }
    const photos = files.map((f) => ({ mediaType: mediaType(f), data: readFileSync(join(folder, f)).toString("base64") }));
    const bookName = expectedModule.bookName ?? String(expectedModule.source).split("(")[0].trim();
    const body = buildRequest({ model: args.model, effort: args.effort, maxTokens: args.maxTokens, skill, units, photos, bookName });
    let result;
    if (args.dryRun) {
      result = stored[recipe.n] ?? { error: "no stored answer" };
      console.log(`recipe ${recipe.n}: request ${Math.round(JSON.stringify(body).length / 1024)} KB, ${files.length} photo(s)`);
    } else {
      result = await callApi(body, key);
      answers[recipe.n] = result;
    }

    const usage = result.usage;
    const dollars = cost(args.model, usage);
    totalCost += dollars;
    if (result.error || !result.recipe) {
      console.log(`recipe ${recipe.n}: ${result.error ?? "no answer"}`);
      rows.push({ n: recipe.n, scores: Object.fromEntries(FIELDS.map((f) => [f, 0])), usage, durationMs: result.durationMs, cost: dollars, error: result.error });
      continue;
    }

    rows.push({ n: recipe.n, scores: scoreRecipe(recipe, result.recipe, expectedModule.source, canonical), usage, durationMs: result.durationMs, cost: dollars });
  }

  const table = formatTable(rows);
  console.log(table.text);
  const inTokens = rows.reduce((s, r) => s + (r.usage?.input_tokens ?? 0), 0);
  const outTokens = rows.reduce((s, r) => s + (r.usage?.output_tokens ?? 0), 0);
  console.log(`tokens in ${inTokens}, out ${outTokens} | estimated cost $${totalCost.toFixed(3)} (opus $4/$20, sonnet $2/$10, haiku $0.10/$0.50 per MTok)`);

  if (args.report) {
    const reportDir = resolve(args.reportDir ?? join(dir, "reports"));
    mkdirSync(reportDir, { recursive: true });
    const name = `${args.book}-${args.model}-${args.effort}-v${skill.version}-${new Date().toISOString().slice(0, 10)}${args.dryRun ? "-dry" : ""}`;
    writeFileSync(join(reportDir, `${name}.json`), JSON.stringify({ book: args.book, model: args.model, effort: args.effort, skillVersion: skill.version, dryRun: args.dryRun, means: table.means, overall: table.overall, inputTokens: inTokens, outputTokens: outTokens, costUsd: totalCost, rows }, null, 2));
    if (!args.dryRun) writeFileSync(join(folder, "answers.json"), JSON.stringify(answers, null, 2));
    console.log(`report: ${join(reportDir, `${name}.json`)}`);
  }
}

if (process.argv[1] && pathToFileURL(resolve(process.argv[1])).href === import.meta.url) {
  await main(process.argv.slice(2));
}
