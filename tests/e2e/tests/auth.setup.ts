import { test as setup } from "@playwright/test";
import { adminState, signIn } from "./fixtures";

// One login per run; every project depends on this and reuses the saved cookies. The app fetches a fresh
// antiforgery token per page load, so a reused auth cookie works with it.
setup("sign in as the seeded admin", async ({ page }) => {
  await signIn(page);
  await page.context().storageState({ path: adminState });
});
