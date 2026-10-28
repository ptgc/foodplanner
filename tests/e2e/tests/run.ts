import { appendFileSync, existsSync, readFileSync } from "node:fs";

/** The tag of this run (set by global-setup.ts): letters only, lower case, in every name a spec creates. */
export function runTag(): string {
  const tag = process.env.E2E_RUN_TAG;
  if (!tag) throw new Error("E2E_RUN_TAG is not set; run the suite through playwright (global-setup.ts sets it).");
  return tag;
}

/** Where the weeks used by this run are listed (one Monday per line), so the teardown knows which weeks to check. */
export function weeksFile(tag: string): string {
  return `.auth/weeks-${tag}.txt`;
}

export function recordWeek(week: string): void {
  appendFileSync(weeksFile(runTag()), week + "\n");
}

export function recordedWeeks(tag: string): string[] {
  const file = weeksFile(tag);
  if (!existsSync(file)) return [];
  return [...new Set(readFileSync(file, "utf8").split("\n").filter(Boolean))];
}
