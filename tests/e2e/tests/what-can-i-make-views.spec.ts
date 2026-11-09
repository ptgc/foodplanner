import type { APIRequestContext, Page } from "@playwright/test";
import { adminState, expect, test, uniqueStamp } from "./fixtures";

test.use({ storageState: adminState });
test.setTimeout(180_000);

// T-100 verifier: scaled servings against the API, layout at 390 px (every control >= 44 px, no sideways scroll), English and
// Norwegian, and cook-today with the chosen servings surviving a reload. Own household, deleted in `finally`.
async function csrf(request: APIRequestContext): Promise<Record<string, string>> {
  const res = await request.get("/api/auth/antiforgery");
  return { "X-XSRF-TOKEN": ((await res.json()) as { token: string }).token };
}

function inventedName(stamp: string, word: string): string {
  return "Zqx" + word + stamp.replace(/\d/g, (d) => "abcdefghij"[Number(d)]!).replace(/-/g, "");
}

async function checkLayout(page: Page, mobile: boolean) {
  if (!mobile) return;
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
  expect(overflow, "no horizontal scroll").toBeLessThanOrEqual(0);
  const screen = page.getByTestId("suggest-screen");
  for (const control of await screen.locator("button, a, select").all()) {
    if (!(await control.isVisible())) continue;
    const box = (await control.boundingBox())!;
    expect(box.height, `touch target ${await control.innerText().catch(() => "")}`).toBeGreaterThanOrEqual(43.5);
  }
}

for (const locale of ["en-US", "nb-NO"] as const) {
  test.describe(locale, () => {
    test.use({ locale });

    test(`scaled amounts match the API, layout holds, cook today keeps the servings (${locale})`, async ({ page }, testInfo) => {
      const request = page.request;
      const mobile = (page.viewportSize()?.width ?? 0) <= 390;
      const stamp = uniqueStamp(testInfo);
      // Screenshots only when SHOT_DIR is set, so a normal run leaves no files behind.
      const shots = process.env.SHOT_DIR;
      const shoot = async (name: string) => {
        if (shots) await page.screenshot({ path: `${shots}/suggest-${locale}-${testInfo.project.name}-${name}.png`, fullPage: true });
      };
      let h: string | undefined;
      try {
        const created = await request.post("/api/households", { headers: await csrf(request), data: { name: `E2E suggest views ${stamp}` } });
        expect(created.status(), await created.text()).toBe(201);
        h = ((await created.json()) as { id: string }).id;
        const me = (await (await request.get("/api/auth/me")).json()) as { userId: string };
        await page.addInitScript(([key, id]) => window.localStorage.setItem(key!, id!), [`foodplanner.activeHousehold.${me.userId}`, h]);
        const units = (await (await request.get("/api/units")).json()) as { id: string; code: string }[];
        const g = units.find((u) => u.code === "g")!.id;
        const ids: Record<string, string> = {};
        for (const word of ["rice", "cheese"]) {
          const name = inventedName(stamp, word);
          const res = await request.post(`/api/households/${h}/ingredients`, { headers: await csrf(request), data: { name, dimension: "Mass", isStaple: false } });
          expect(res.status(), await res.text()).toBe(201);
          ids[word] = ((await res.json()) as { id: string }).id;
        }
        const title = `Rice dish ${stamp}`;
        const rec = await request.post(`/api/households/${h}/recipes`, {
          headers: await csrf(request),
          data: {
            title,
            description: null,
            servings: 4,
            prepMinutes: 5,
            cookMinutes: 10,
            steps: [{ text: "Cook" }],
            ingredients: [
              { ingredientId: ids.rice, quantity: 300, unitId: g, note: null },
              { ingredientId: ids.cheese, quantity: 100, unitId: g, note: null },
            ],
            tagIds: [],
          },
        });
        expect(rec.status(), await rec.text()).toBe(201);
        const put = await request.put(`/api/households/${h}/pantry/${ids.rice}`, {
          headers: await csrf(request),
          data: { quantity: 500, unitId: g, expiresAt: null },
        });
        expect(put.status(), await put.text()).toBeLessThan(300);

        await page.goto("/what-can-i-make");
        const card = page.getByTestId("suggest-card").filter({ hasText: title });
        await expect(card).toHaveCount(1);
        await checkLayout(page, mobile);
        await shoot("own");

        // 8 servings: rice 600 g vs 500 g at home -> 100 g more; cheese 200 g. Compare with the API.
        const api = await (await request.get(`/api/households/${h}/what-can-i-make?servings=8`)).json();
        const mine = (api.items as { title: string; missing: { reason: string; quantities: { amount: number }[] }[] }[]).find((i) => i.title === title)!;
        expect(mine.missing.map((m) => `${m.reason}:${m.quantities[0]!.amount}`).sort()).toEqual(["NotAtHome:200", "NotEnough:100"]);
        await page.getByLabel(locale === "en-US" ? "Servings" : "Porsjoner").selectOption("8");
        const lines = card.getByTestId("suggest-missing");
        await expect(lines).toContainText("200 g");
        await expect(lines).toContainText("100 g");
        await expect(lines).toContainText(locale === "en-US" ? "more" : "mer");
        await checkLayout(page, mobile);
        await shoot("8");

        // Cook today at 8: the plan holds 8 servings; after a reload the badge shows and add-missing is offered.
        await card.getByRole("button", { name: /Cook today|Lag i dag/ }).click();
        await expect(card.getByTestId("suggest-planned-today")).toBeVisible();
        const weekStart = ((await (await request.get(`/api/households/${h}/what-can-i-make`)).json()) as { weekStart: string }).weekStart;
        const plan = await request.get(`/api/households/${h}/meal-plans/${weekStart}`);
        expect(await plan.text()).toContain('"servings":8');
        await page.reload();
        const again = page.getByTestId("suggest-card").filter({ hasText: title });
        await expect(again.getByTestId("suggest-planned-today")).toBeVisible();
        await expect(again.getByRole("button", { name: /Add missing|Legg det som mangler/ })).toBeVisible();
        await expect(again.getByRole("button", { name: /Cook today|Lag i dag/ })).toHaveCount(0);
        await checkLayout(page, mobile);
        await shoot("planned");

        // The card is honest about the servings: it says 8 (from the plan, after the reload) and shows no amounts.
        await expect(again.getByTestId("suggest-planned-servings")).toContainText(
          locale === "en-US" ? "Planned today for 8 servings" : "Planlagt i dag for 8 porsjoner",
        );
        await expect(again.getByTestId("suggest-missing")).not.toContainText(/\d+ g\b/);
        // Add missing uses the planned 8 servings: cheese 200 g, and rice 100 g (600 g needed, 500 g at home).
        await again.getByRole("button", { name: /Add missing|Legg det som mangler/ }).click();
        await expect(again.getByTestId("suggest-added-note")).toContainText("200 g");
        await expect(again.getByTestId("suggest-added-note")).toContainText("100 g");
        const list = (await (await request.get(`/api/households/${h}/shopping-lists/${weekStart}`)).json()) as {
          main: { name: string; quantities: { amount: number; unitCode: string }[] }[];
        };
        const amounts = Object.fromEntries(list.main.map((i) => [i.name, `${i.quantities[0]!.amount} ${i.quantities[0]!.unitCode}`]));
        expect(amounts[inventedName(stamp, "cheese")]).toBe("200 g");
        expect(amounts[inventedName(stamp, "rice")]).toBe("100 g");
      } finally {
        if (h) await request.delete(`/api/households/${h}`, { headers: await csrf(request) });
      }
    });
  });
}
