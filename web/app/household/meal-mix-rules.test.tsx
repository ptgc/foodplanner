import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { resetAntiforgeryToken } from "../api/client";
import { t } from "../test-i18n";
import { MealMixRules, sumOf, toMinimums, toRules } from "./meal-mix-rules";

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": status >= 400 ? "application/problem+json" : "application/json" },
  });

let saved: { foodType: string; minPerWeek: number }[];
let puts: unknown[];
let failure: Response | null;

beforeEach(() => {
  resetAntiforgeryToken();
  saved = [{ foodType: "Fish", minPerWeek: 1 }];
  puts = [];
  failure = null;
  vi.stubGlobal(
    "fetch",
    vi.fn(async (request: Request) => {
      const path = new URL(request.url).pathname;
      if (path === "/api/auth/antiforgery") return json({ token: "t" });
      if (path.endsWith("/meal-mix-rules")) {
        if (request.method === "GET") return json({ rules: saved, dinnerDaysPerWeek: 7 });
        const body = (await request.json()) as { rules: typeof saved };
        puts.push(body);
        if (failure) return failure;
        saved = body.rules;
        return json({ rules: saved, dinnerDaysPerWeek: 7 });
      }
      throw new Error(`unexpected ${request.method} ${path}`);
    }),
  );
});
afterEach(() => {
  vi.unstubAllGlobals();
});

const row = (type: string) => document.querySelector<HTMLElement>(`[data-testid="mix-row"][data-food-type="${type}"]`)!;

describe("meal mix helpers", () => {
  it("turns rules into minimums and back, dropping zeros", () => {
    const minimums = toMinimums([
      { foodType: "Vegetarian", minPerWeek: 2 },
      { foodType: "Fish", minPerWeek: 1 },
    ]);
    expect(sumOf(minimums)).toBe(3);
    expect(toRules({ ...minimums, Beef: 0 })).toEqual([
      { foodType: "Fish", minPerWeek: 1 },
      { foodType: "Vegetarian", minPerWeek: 2 },
    ]);
  });
});

describe("MealMixRules", () => {
  it("shows the saved rules and lets an adult change and save them", async () => {
    const user = userEvent.setup();
    render(<MealMixRules householdId="h1" canEdit />);
    await screen.findAllByTestId("mix-row");
    expect(within(row("Fish")).getByTestId("mix-value")).toHaveTextContent(t("household.mix.atLeast", { count: 1 }));
    expect(within(row("Vegetarian")).getByTestId("mix-value")).toHaveTextContent(t("household.mix.none"));
    expect(document.querySelector('[data-food-type="Unknown"]')).toBeNull();

    await user.click(within(row("Vegetarian")).getByRole("button", { name: t("household.mix.more", { type: t("foodType.Vegetarian") }) }));
    await user.click(within(row("Fish")).getByRole("button", { name: t("household.mix.fewer", { type: t("foodType.Fish") }) }));
    expect(screen.getByTestId("mix-total")).toHaveTextContent(t("household.mix.total", { total: 1, days: 7 }));
    await user.click(screen.getByRole("button", { name: t("household.mix.save") }));

    await waitFor(() => expect(screen.getByTestId("mix-saved")).toBeInTheDocument());
    expect(puts).toEqual([{ rules: [{ foodType: "Vegetarian", minPerWeek: 1 }] }]);
  });

  it("stops adding when the minimums fill the week", async () => {
    saved = [{ foodType: "Fish", minPerWeek: 7 }];
    render(<MealMixRules householdId="h1" canEdit />);
    await screen.findAllByTestId("mix-row");
    expect(within(row("Beef")).getByRole("button", { name: t("household.mix.more", { type: t("foodType.Beef") }) })).toBeDisabled();
  });

  it("shows a translated message when the save fails", async () => {
    failure = json({ status: 409, title: "x", type: "urn:foodplanner:problem:meal-mix-rules-conflict" }, 409);
    const user = userEvent.setup();
    render(<MealMixRules householdId="h1" canEdit />);
    await screen.findAllByTestId("mix-row");
    await user.click(screen.getByRole("button", { name: t("household.mix.save") }));
    expect(await screen.findByRole("alert")).toHaveTextContent(t("household.mix.problem.conflict"));
  });

  it("is read-only for a member who cannot edit", async () => {
    render(<MealMixRules householdId="h1" canEdit={false} />);
    await screen.findAllByTestId("mix-row");
    expect(screen.queryByRole("button", { name: t("household.mix.save") })).toBeNull();
    expect(screen.getByText(t("household.mix.adultsOnly"))).toBeInTheDocument();
  });
});
