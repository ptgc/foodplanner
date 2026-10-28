import { mkdirSync, writeFileSync } from "node:fs";
import { dirname } from "node:path";
import { weeksFile } from "./tests/run";

// T-083: one tag per run, put in every name a spec creates (uniqueStamp) so the teardown can find leftovers.
// Workers inherit the environment of this process, so the variable set here reaches every test.
export default function globalSetup() {
  const random = Array.from({ length: 6 }, () => String.fromCharCode(97 + Math.floor(Math.random() * 26))).join("");
  const tag = process.env.E2E_RUN_TAG || `ee${random}`;
  process.env.E2E_RUN_TAG = tag;
  mkdirSync(dirname(weeksFile(tag)), { recursive: true });
  writeFileSync(weeksFile(tag), "");
}
