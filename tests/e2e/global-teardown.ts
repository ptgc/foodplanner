import { request } from "@playwright/test";
import { existsSync, rmSync } from "node:fs";
import { formatLeftovers, runMatcher, sweep } from "./cleanup/sweep.ts";
import { recordedWeeks, weeksFile } from "./tests/run";

// T-083: after the whole run, nothing that carries the run's tag may remain. Leftovers are removed (so the shared
// database stays tidy) and then reported as a failure: a spec that leaves data behind has a bug.
export default async function globalTeardown() {
  const tag = process.env.E2E_RUN_TAG;
  if (!tag) return;
  const state = ".auth/admin.json";
  if (!existsSync(state)) return; // the run never signed in, so it created nothing
  const baseURL = process.env.BASE_URL ?? "http://localhost:3000";
  const api = await request.newContext({ baseURL, storageState: state });
  try {
    const found = await sweep({ request: api, matches: runMatcher(tag), dryRun: false, weeks: recordedWeeks(tag) });
    console.log(`\ne2e cleanup check (run tag ${tag}): ${formatLeftovers(found)}`);
    rmSync(weeksFile(tag), { force: true });
    if (found.length > 0) throw new Error(`The e2e run left ${found.length} item(s) behind (removed now, but fix the spec):\n${formatLeftovers(found)}`);
  } finally {
    await api.dispose();
  }
}
