import { request } from "@playwright/test";
import { formatLeftovers, legacyMatcher, runMatcher, sweep } from "./sweep.ts";

// T-083: one-off cleanup of e2e leftovers in the local database. Run it through scripts/e2e-cleanup.sh (or .ps1).
//   --dry-run        list what would be removed, delete nothing (exit 1 when anything is found)
//   --tag <tag>      only data carrying that run tag (default: the legacy E2E naming patterns of runs before the tag)
// Credentials come from E2E_ADMIN_EMAIL / E2E_ADMIN_PASSWORD; the sweep only touches the admin's households and only
// data whose name matches.
const args = process.argv.slice(2);
const dryRun = args.includes("--dry-run");
const tagIndex = args.indexOf("--tag");
const tag = tagIndex >= 0 ? args[tagIndex + 1] : undefined;
const baseURL = process.env.BASE_URL ?? "http://localhost:3000";
const email = process.env.E2E_ADMIN_EMAIL;
const password = process.env.E2E_ADMIN_PASSWORD;
if (!email || !password) {
  console.error("Set E2E_ADMIN_EMAIL and E2E_ADMIN_PASSWORD (use scripts/e2e-cleanup.sh).");
  process.exit(2);
}

const api = await request.newContext({ baseURL });
try {
  const token = (await (await api.get("/api/auth/antiforgery")).json()) as { token: string };
  const login = await api.post("/api/auth/login", { headers: { "X-XSRF-TOKEN": token.token }, data: { email, password } });
  if (!login.ok()) throw new Error(`Login answered ${login.status()}`);
  if (args.includes("--counts")) {
    // Read-only totals, to compare before and after a run.
    const households = (await (await api.get("/api/households")).json()) as { id: string; name: string }[];
    const counts: Record<string, unknown> = { households: households.length };
    for (const h of households) {
      const recipes = (await (await api.get(`/api/households/${h.id}/recipes?limit=1`)).json()) as { total: number };
      const own = (await (await api.get(`/api/households/${h.id}/ingredients/own`)).json()) as unknown[];
      const drafts = (await (await api.get(`/api/households/${h.id}/imports`)).json()) as unknown[];
      counts[h.name] = { recipes: recipes.total, ownIngredients: own.length, importDrafts: drafts.length };
    }
    console.log(JSON.stringify(counts));
    process.exit(0);
  }
  const matches = tag ? runMatcher(tag) : legacyMatcher;
  // Weeks are only known for a tagged run; far-future shopping lists and plans of older runs are invisible in the app.
  const found = await sweep({ request: api, matches, dryRun, weeks: [] });
  console.log(`${dryRun ? "Dry run: " : ""}${tag ? `run tag ${tag}` : "legacy e2e names"}: ${formatLeftovers(found)}`);
  const left = found.filter((f) => !f.removed);
  process.exitCode = left.length > 0 ? 1 : 0;
} finally {
  await api.dispose();
}
