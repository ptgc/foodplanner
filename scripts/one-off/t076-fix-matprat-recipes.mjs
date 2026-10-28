// One-off data fix for T-076 (run once per environment, after the AddIngredientKindPurposeAlternatives migration).
//
// The recipes imported from matprat.no before T-076 hold "margarin til steking" as ingredient + note, and a missing quantity for
// "salt etter smak". Their import records no longer hold the original text (Q-017), so they cannot be re-parsed. This script applies
// the same rules as the importer (IngredientLineParser) to each line's note and ingredient and saves the recipe through the API:
//   - "til steking" etc. -> Purpose Frying; "til servering" -> Serving; "til pynt" -> Garnish; "etter smak" -> ToTaste + Seasoning
//   - a line with no quantity: ToTaste (+ Seasoning), or AsNeeded when it is for frying/serving/garnish
//   - "(kan sløyfes)" / "valgfritt" / "evt." alone -> optional
//   - "eller X" / "evt. X" -> alternative, when an ingredient with exactly that name exists in the catalog or the household's own
//     (otherwise the text stays in the note)
// Only recipes whose source URL is on matprat.no are touched. It is idempotent: a second run changes nothing.
// It goes through the API as the seeded admin (no SQL); the credentials come from user secrets and are never printed.
//
//   node scripts/one-off/t076-fix-matprat-recipes.mjs [--base http://localhost:3000] [--dry-run]
import { execFileSync } from "node:child_process";

const args = process.argv.slice(2);
const dryRun = args.includes("--dry-run");
const base = (args.includes("--base") ? args[args.indexOf("--base") + 1] : "http://localhost:3000").replace(/\/$/, "");

function secret(name) {
  const out = execFileSync("dotnet", ["user-secrets", "list", "--project", "src/Api"], { encoding: "utf8" });
  const match = out.split(/\r?\n/).find((line) => line.startsWith(`${name} = `));
  if (!match) throw new Error(`user secret ${name} is not set`);
  return match.slice(name.length + 3);
}

// ---- tiny cookie-aware fetch -------------------------------------------------------------------------------------------
const jar = new Map();
function remember(response) {
  for (const header of response.headers.getSetCookie?.() ?? []) {
    const [pair] = header.split(";");
    const at = pair.indexOf("=");
    jar.set(pair.slice(0, at), pair.slice(at + 1));
  }
}
async function call(method, path, body, headers = {}) {
  const response = await fetch(base + path, {
    method,
    headers: {
      cookie: [...jar].map(([k, v]) => `${k}=${v}`).join("; "),
      ...(body === undefined ? {} : { "content-type": "application/json" }),
      ...headers,
    },
    body: body === undefined ? undefined : JSON.stringify(body),
    redirect: "manual",
  });
  remember(response);
  const text = await response.text();
  return { status: response.status, json: text ? JSON.parse(text) : null };
}
let token = null;
const write = async (method, path, body) => {
  if (!token) token = (await call("GET", "/api/auth/antiforgery")).json.token;
  return call(method, path, body, { "X-XSRF-TOKEN": token });
};

// ---- the rules (same phrases as IngredientLineParser) ----------------------------------------------------------------
const ci = (source) => new RegExp(`(?<![\\p{L}\\p{N}])(?:${source})(?![\\p{L}\\p{N}])`, "giu");
const phrases = {
  toTaste: ci("etter\\s+smak|to\\s+taste"),
  frying: ci("til\\s+(?:å\\s+)?(?:steking|stekning|steiking|steke(?:\\s+i)?)|å\\s+steke\\s+i|for\\s+(?:pan[\\s-]?)?frying"),
  serving: ci("til\\s+(?:å\\s+)?servering|til\\s+å\\s+servere|for\\s+serving|to\\s+serve(?:\\s+with)?"),
  garnish: ci("til\\s+pynt(?:ing)?|til\\s+dekorasjon|til\\s+garnityr|(?:for|to|as)\\s+garnish(?:ing)?"),
  asNeeded: ci("etter\\s+behov|ved\\s+behov|as\\s+needed|as\\s+required"),
  optional: ci("kan\\s+(?:sløyfes|utelates|droppes|sløyfe)|valgfri(?:tt)?|optional|if\\s+desired"),
};
const keptAsNeeded = ci("til\\s+pensling|til\\s+smøring|til\\s+å\\s+(?:smøre|pensle)|for\\s+(?:greasing|brushing)");
const alternativeLead = /^(eller|or|evt\.?|eventuelt)\s+(.+)$/iu;
const loneOptional = /^(?:evt|ev|eventuelt)\.?$/iu;

/** Splits the note into fragments and reads what each one says. Returns the new structure of the line, minus alternative ids. */
function readNote(note) {
  const flags = { toTaste: false, frying: false, serving: false, garnish: false, asNeeded: false, optional: false };
  const alternatives = []; // { text, noteText }
  const kept = [];
  const fragments = (note ?? "").split(/[;,]/).map((f) => f.trim()).filter(Boolean);
  for (let fragment of fragments) {
    for (const [name, regex] of Object.entries(phrases)) {
      fragment = fragment.replace(regex, () => {
        flags[name] = true;
        return " ";
      });
    }
    fragment = fragment.replace(keptAsNeeded, (m) => {
      flags.asNeeded = true;
      kept.push(m);
      return " ";
    });
    fragment = fragment.replace(/\s+/g, " ").trim().replace(/^[-.:]+|[-.:]+$/g, "").trim();
    if (!fragment) continue;
    if (loneOptional.test(fragment)) {
      flags.optional = true;
      continue;
    }
    const lead = alternativeLead.exec(fragment);
    if (lead && !/\d/.test(lead[2]) && lead[2].split(/\s+/).length <= 5) {
      // "eller margarin/olje" is one fragment of the note; it is split on eller/or and "/". The marker is kept as written.
      for (const part of lead[2].split(/\s+(?:eller|or)\s+|\s*\/\s*/iu)) alternatives.push({ text: part.trim(), noteText: `${lead[1]} ${part.trim()}` });
      continue;
    }
    kept.push(fragment);
  }
  const said = Object.values(flags).some(Boolean);
  return { flags, alternatives, kept, said };
}

function structure(line, read) {
  const { flags } = read;
  const measured = line.quantity !== null && line.quantity !== undefined;
  const said = flags.frying ? "Frying" : flags.serving ? "Serving" : flags.garnish ? "Garnish" : flags.toTaste ? "Seasoning" : null;
  // What an earlier run (or a person) already set stays when the note no longer says anything: that makes a second run a no-op.
  const purpose = said ?? (line.purpose !== "Main" ? line.purpose : null);
  if (measured) return { amountKind: "Measured", purpose: purpose ?? "Main" };
  if (flags.toTaste) return { amountKind: "ToTaste", purpose: purpose ?? "Seasoning" };
  if (flags.asNeeded || said || line.amountKind === "AsNeeded") return { amountKind: "AsNeeded", purpose: purpose ?? "Main" };
  return { amountKind: "ToTaste", purpose: purpose ?? "Seasoning" }; // no amount, no phrase
}

// ---- main ------------------------------------------------------------------------------------------------------------------
const login = await call("GET", "/api/auth/antiforgery");
token = login.json.token;
const signedIn = await write("POST", "/api/auth/login", { email: secret("Seed:AdminEmail"), password: secret("Seed:AdminPassword") });
if (signedIn.status >= 300) throw new Error(`login failed (${signedIn.status})`);
token = null; // a new token after sign-in

const households = (await call("GET", "/api/households")).json;
const report = [];
let touched = 0;
for (const household of households) {
  const h = household.id;
  const names = new Map(); // lower-case name -> id, filled on demand
  const findIngredient = async (text) => {
    const key = text.toLocaleLowerCase("nb");
    if (!names.has(key)) {
      const { json } = await call("GET", `/api/households/${h}/ingredients?search=${encodeURIComponent(text)}`, undefined, { "Accept-Language": "nb" }); // names are matched in Norwegian (the note text is Norwegian)
      names.set(key, (json ?? []).find((i) => i.name.toLocaleLowerCase("nb") === key)?.id ?? null);
    }
    return names.get(key);
  };

  for (let offset = 0; ; offset += 100) {
    const page = (await call("GET", `/api/households/${h}/recipes?limit=100&offset=${offset}`)).json;
    for (const item of page.items) {
      const recipe = (await call("GET", `/api/households/${h}/recipes/${item.id}`)).json;
      let host = null;
      try {
        host = recipe.sourceUrl ? new URL(recipe.sourceUrl).hostname.replace(/^www\./, "") : null;
      } catch {
        host = null;
      }
      if (host !== "matprat.no") continue; // no other recipe is touched (hand-made, cookbooks, other sites)

      const changes = [];
      const ingredients = [];
      for (const line of recipe.ingredients) {
        const read = readNote(line.note);
        const { amountKind, purpose } = structure(line, read);
        const alternativeIds = line.alternatives.map((a) => a.ingredientId);
        const kept = [...read.kept];
        let matchedAlternative = false;
        for (const alt of read.alternatives) {
          const id = await findIngredient(alt.text);
          if (id && id !== line.ingredientId && !alternativeIds.includes(id) && alternativeIds.length < 5) {
            alternativeIds.push(id);
            matchedAlternative = true;
          } else if (!id || id === line.ingredientId) kept.push(alt.noteText);
        }
        // The note is rewritten only when a phrase or alternative was taken out of it; otherwise it stays exactly as it was.
        const note = read.said || matchedAlternative ? (kept.length ? kept.join("; ").slice(0, 200) : null) : (line.note ?? null);
        const next = {
          ingredientId: line.ingredientId,
          quantity: amountKind === "Measured" ? line.quantity : null,
          unitId: amountKind === "Measured" ? line.unitId : null,
          note,
          amountKind,
          purpose,
          isOptional: line.isOptional || read.flags.optional,
          alternativeIds,
        };
        // Idempotent: only a line whose structure or note differs is a change. A line the importer never saw is "Main/Measured" with its
        // note; an already fixed line parses to itself.
        const before = `${line.amountKind}/${line.purpose}${line.isOptional ? "/optional" : ""}${line.alternatives.length ? `/alt:${line.alternatives.map((a) => a.ingredientName).join("+")}` : ""} note=${line.note ?? ""}`;
        const after = `${next.amountKind}/${next.purpose}${next.isOptional ? "/optional" : ""}${alternativeIds.length ? `/alt:${alternativeIds.length}` : ""} note=${note ?? ""}`;
        const same =
          next.amountKind === line.amountKind && next.purpose === line.purpose && next.isOptional === line.isOptional &&
          (note ?? null) === (line.note ?? null) && alternativeIds.join() === line.alternatives.map((a) => a.ingredientId).join();
        if (!same) changes.push(`${line.ingredientName}: ${before}  =>  ${after}`);
        ingredients.push(next);
      }
      if (changes.length === 0) {
        report.push({ title: recipe.title, changes: [] });
        continue;
      }
      if (!dryRun) {
        const body = {
          title: recipe.title,
          description: recipe.description,
          servings: recipe.servings,
          prepMinutes: recipe.prepMinutes,
          cookMinutes: recipe.cookMinutes,
          steps: recipe.steps.map((s) => ({ text: s.text })),
          ingredients,
          tagIds: recipe.tags.map((t) => t.id),
          sourceUrl: recipe.sourceUrl,
          sourceName: recipe.sourceName,
        };
        const saved = await write("PUT", `/api/households/${h}/recipes/${recipe.id}`, body);
        if (saved.status !== 200) throw new Error(`saving "${recipe.title}" failed (${saved.status}) ${JSON.stringify(saved.json?.errors ?? saved.json?.title ?? "")}`);
      }
      touched += 1;
      report.push({ title: recipe.title, changes });
    }
    if (offset + 100 >= page.total) break;
  }
}

console.log(`${dryRun ? "DRY RUN: " : ""}matprat.no recipes found: ${report.length}, changed: ${touched}`);
for (const r of report) {
  console.log(`\n${r.title}${r.changes.length === 0 ? "  (nothing to change)" : ""}`);
  for (const c of r.changes) console.log(`  - ${c}`);
}
