import type { APIRequestContext, Page, Route } from "@playwright/test";
import { adminState, expect, test, uniqueStamp } from "./fixtures";

test.use({ storageState: adminState });
test.setTimeout(120_000);

// T-077 UI verifier additions: the LimitReached and Unavailable notes (no line mark) in en and nb, and the edit that
// clears the mark answered by the REAL API. Only the POST and the review's GET are patched with route interception
// (the compose stack runs with the AI off); the PUT goes through to the real API.

async function csrf(request: APIRequestContext): Promise<Record<string, string>> {
  const res = await request.get("/api/auth/antiforgery");
  return { "X-XSRF-TOKEN": ((await res.json()) as { token: string }).token };
}

async function noHorizontalScroll(page: Page) {
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
  expect(overflow, "no horizontal scroll").toBeLessThanOrEqual(0);
}

type Draft = { aiAssist: string; lines: { aiSuggested: boolean }[] };

const privacy = {
  "en-US": "If AI help is on, lines we can't read may be sent to Anthropic (no names or account data).",
  "nb-NO": "Hvis KI-hjelp er slått på, kan linjer vi ikke klarer å lese, bli sendt til Anthropic (uten navn eller kontodata).",
} as const;

const x = {
  "en-US": {
    tab: "Pasted text",
    textLabel: "Recipe text",
    read: "Read recipe",
    heading: "Check the recipe",
    LimitReached: "AI help is used up for now (daily or monthly limit). The lines that could not be read are left for you to fill in.",
    Unavailable: "AI help was not available this time.",
  },
  "nb-NO": {
    tab: "Limt inn tekst",
    textLabel: "Oppskriftstekst",
    read: "Les oppskriften",
    heading: "Sjekk oppskriften",
    LimitReached: "KI-hjelpen er brukt opp for nå (daglig eller månedlig grense). Linjene som ikke kunne leses, er overlatt til deg å fylle ut.",
    Unavailable: "KI-hjelpen var ikke tilgjengelig denne gangen.",
  },
} as const;

async function patchAnswers(page: Page, status: string, markFirst: boolean) {
  const patch = async (route: Route) => {
    const response = await route.fetch();
    if (!response.ok()) return route.fulfill({ response });
    const body = (await response.json()) as Draft;
    body.aiAssist = status;
    if (markFirst && body.lines[0]) body.lines[0].aiSuggested = true;
    return route.fulfill({ response, json: body });
  };
  await page.route(/\/api\/households\/[^/]+\/imports$/, (route) => (route.request().method() === "POST" ? patch(route) : route.continue()));
  await page.route(/\/api\/households\/[^/]+\/imports\/[0-9a-f-]{36}$/, (route) => (route.request().method() === "GET" ? patch(route) : route.continue()));
}

async function cleanup(request: APIRequestContext, page: Page, title: string) {
  const households = (await (await request.get("/api/households")).json()) as { id: string }[];
  const h = households[0]!.id;
  const fromUrl = new URL(page.url()).searchParams.get("draft") ?? undefined;
  const drafts = (await (await request.get(`/api/households/${h}/imports`)).json()) as { id: string; title: string }[];
  const id = fromUrl ?? drafts.find((d) => d.title === title)?.id;
  if (id) await request.delete(`/api/households/${h}/imports/${id}`, { headers: await csrf(request) });
}

for (const locale of ["en-US", "nb-NO"] as const) {
  for (const status of ["LimitReached", "Unavailable"] as const) {
    test.describe(`${locale} ${status}`, () => {
      test.use({ locale });

      test(`the ${status} note shows without any line mark (${locale})`, async ({ page }, testInfo) => {
        const t = x[locale];
        const shots = process.env.SHOT_DIR;
        const title = `E2E AI note ${status} ${testInfo.project.name} ${uniqueStamp(testInfo)}`;
        await patchAnswers(page, status, false);
        try {
          await page.goto("/recipes/import");
          await expect(page.getByTestId("ai-privacy-start")).toContainText(privacy[locale]);
          if (shots) await page.screenshot({ path: `${shots}/import-start-${locale}-${testInfo.project.name}.png`, fullPage: true });
          await page.getByRole("tab", { name: t.tab }).click();
          await page.getByLabel(t.textLabel).fill([title, "Ingredients", "2 dl melk", "Method", "Stir."].join("\n"));
          await expect(page.getByTestId("ai-privacy-start")).toContainText(privacy[locale]); // also on the text path
          await page.getByRole("button", { name: t.read }).click();
          await expect(page.getByRole("heading", { name: t.heading, level: 1 })).toBeVisible();
          const note = page.getByTestId("ai-note");
          await expect(note).toContainText(t[status]);
          await expect(note).toContainText(privacy[locale]);
          await expect(page.getByTestId("ai-mark")).toHaveCount(0);
          await noHorizontalScroll(page);
          if (shots) await page.screenshot({ path: `${shots}/import-${status}-${locale}-${testInfo.project.name}.png`, fullPage: true });
        } finally {
          await cleanup(page.request, page, title);
        }
      });
    });
  }
}

test.describe("en-US edit clears the mark", () => {
  test.use({ locale: "en-US" });

  test("a real PUT answers aiSuggested false and the card drops the mark", async ({ page }, testInfo) => {
    const title = `E2E AI edit ${testInfo.project.name} ${uniqueStamp(testInfo)}`;
    await patchAnswers(page, "Applied", true);
    try {
      await page.goto("/recipes/import");
      await page.getByRole("tab", { name: x["en-US"].tab }).click();
      await page.getByLabel(x["en-US"].textLabel).fill([title, "Ingredients", "2 dl melk", "3 egg", "Method", "Stir."].join("\n"));
      await page.getByRole("button", { name: x["en-US"].read }).click();
      await expect(page.getByTestId("ai-mark")).toHaveCount(1);
      const put = page.waitForResponse((r) => r.request().method() === "PUT" && /\/imports\/[0-9a-f-]{36}$/.test(r.url()));
      await page.getByRole("button", { name: "Change line 1" }).click();
      await page.getByRole("button", { name: "Use this line" }).first().click();
      const body = (await (await put).json()) as Draft;
      expect(body.lines[0]!.aiSuggested).toBe(false); // the real API cleared it
      await expect(page.getByTestId("ai-mark")).toHaveCount(0); // and the card follows the answer
    } finally {
      await cleanup(page.request, page, title);
    }
  });
});
