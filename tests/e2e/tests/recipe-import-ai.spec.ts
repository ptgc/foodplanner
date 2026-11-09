import type { APIRequestContext, Page } from "@playwright/test";
import { adminState, expect, test, uniqueStamp } from "./fixtures";

test.use({ storageState: adminState });
test.setTimeout(120_000);

// The compose stack runs with the AI fallback off (AiImport:Enabled false), so a real `Applied` cannot happen here.
// The import answers (POST and the review's GET) are real answers patched with route interception: aiAssist Applied
// and the first line marked aiSuggested. Everything else (the draft, the edit that clears the mark would be the
// server's) is real, and the draft is deleted at the end.

async function csrf(request: APIRequestContext): Promise<Record<string, string>> {
  const res = await request.get("/api/auth/antiforgery");
  return { "X-XSRF-TOKEN": ((await res.json()) as { token: string }).token };
}

async function noHorizontalScroll(page: Page) {
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
  expect(overflow, "no horizontal scroll").toBeLessThanOrEqual(0);
}

type Draft = { aiAssist: string; lines: { aiSuggested: boolean }[] };

const texts = {
  "en-US": {
    tab: "Pasted text",
    textLabel: "Recipe text",
    read: "Read recipe",
    heading: "Check the recipe",
    note: "Some lines were filled in by AI. Check them before saving.",
    privacy: "If AI help is on, lines we can't read may be sent to Anthropic (no names or account data).",
    mark: "AI suggestion, please check",
    label: "Line 1 was filled in by AI. Please check it.",
  },
  "nb-NO": {
    tab: "Limt inn tekst",
    textLabel: "Oppskriftstekst",
    read: "Les oppskriften",
    heading: "Sjekk oppskriften",
    note: "Noen linjer er fylt ut av KI. Sjekk dem før du lagrer.",
    privacy: "Hvis KI-hjelp er slått på, kan linjer vi ikke klarer å lese, bli sendt til Anthropic (uten navn eller kontodata).",
    mark: "KI-forslag, vennligst sjekk",
    label: "Linje 1 er fylt ut av KI. Sjekk den.",
  },
} as const;

for (const locale of ["en-US", "nb-NO"] as const) {
  test.describe(locale, () => {
    test.use({ locale });

    test(`the review shows the AI mark and note, and the privacy text (${locale})`, async ({ page }, testInfo) => {
      const request = page.request;
      const x = texts[locale];
      const mobile = (page.viewportSize()?.width ?? 0) <= 390;
      const shots = process.env.SHOT_DIR; // screenshots only when asked, so a normal run leaves no files
      const households = (await (await request.get("/api/households")).json()) as { id: string }[];
      const h = households[0]!.id;
      const title = `E2E AI import ${testInfo.project.name} ${uniqueStamp(testInfo)}`;
      let draftId: string | undefined;

      const patch = async (route: import("@playwright/test").Route) => {
        const response = await route.fetch();
        if (!response.ok()) return route.fulfill({ response });
        const body = (await response.json()) as Draft;
        body.aiAssist = "Applied";
        if (body.lines[0]) body.lines[0].aiSuggested = true;
        return route.fulfill({ response, json: body });
      };
      await page.route(/\/api\/households\/[^/]+\/imports$/, (route) => (route.request().method() === "POST" ? patch(route) : route.continue()));
      await page.route(/\/api\/households\/[^/]+\/imports\/[0-9a-f-]{36}$/, (route) => (route.request().method() === "GET" ? patch(route) : route.continue()));

      try {
        await page.goto("/recipes/import");
        await page.getByRole("tab", { name: x.tab }).click();
        await page.getByLabel(x.textLabel).fill([title, "Ingredients", "2 dl melk", "3 egg", "Method", "Stir."].join("\n"));
        await page.getByRole("button", { name: x.read }).click();
        await expect(page.getByRole("heading", { name: x.heading, level: 1 })).toBeVisible();
        draftId = new URL(page.url()).searchParams.get("draft") ?? undefined;

        const note = page.getByTestId("ai-note");
        await expect(note).toContainText(x.note);
        await expect(note).toContainText(x.privacy);
        const marks = page.getByTestId("ai-mark");
        await expect(marks).toHaveCount(1);
        await expect(marks).toContainText(x.mark);
        await expect(page.getByTestId("import-line").first().getByRole("note", { name: x.label })).toBeVisible();

        if (mobile) {
          await noHorizontalScroll(page);
          for (const button of [page.getByRole("button", { name: /^(Save recipe|Lagre oppskrift)$/ }), page.getByTestId("import-line").first().getByRole("button").first()]) {
            expect((await button.boundingBox())!.height).toBeGreaterThanOrEqual(44);
          }
        }
        if (shots) await page.screenshot({ path: `${shots}/import-ai-${locale}-${testInfo.project.name}.png`, fullPage: true });
      } finally {
        if (!draftId) {
          const drafts = (await (await request.get(`/api/households/${h}/imports`)).json()) as { id: string; title: string }[];
          draftId = drafts.find((d) => d.title === title)?.id;
        }
        if (draftId) await request.delete(`/api/households/${h}/imports/${draftId}`, { headers: await csrf(request) });
      }
    });
  });
}
