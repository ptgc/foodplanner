import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { resetAntiforgeryToken } from "../api/client";
import type { components } from "../api/schema";
import { t } from "../test-i18n";
import { parseAmount, WeeklyGroceries } from "./weekly-groceries";

type Weekly = components["schemas"]["WeeklyGroceryResponse"];

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": status >= 400 ? "application/problem+json" : "application/json" },
  });

const milk: Weekly = { id: "w1", ingredientId: "i-milk", name: "Melk", section: "Dairy", quantity: 1.5, unitId: "u-l", unitCode: "l", unitName: "liter" };
const bread: Weekly = { id: "w2", ingredientId: "i-bread", name: "Brød", section: "DryGoods", quantity: null, unitId: null, unitCode: null, unitName: null };
const units = [
  { id: "u-l", code: "l", name: "Liter", dimension: "Volume", factorToBase: 1000 },
  { id: "u-g", code: "g", name: "Gram", dimension: "Mass", factorToBase: 1 },
];

type Call = { method: string; path: string; body: unknown };
let items: Weekly[];
let calls: Call[];
let failure: Response | null;

beforeEach(() => {
  resetAntiforgeryToken();
  items = [milk, bread];
  calls = [];
  failure = null;
  vi.stubGlobal(
    "fetch",
    vi.fn(async (request: Request) => {
      const path = new URL(request.url).pathname;
      const method = request.method;
      if (path === "/api/auth/antiforgery") return json({ token: "t" });
      if (path === "/api/units") return json(units);
      if (path.endsWith("/ingredients") && method === "GET") {
        return json([{ id: "i-ost", name: "Ost", isStaple: false, dimension: "Mass", isGlobal: true, section: "Other" }]);
      }
      const body = method === "GET" || method === "DELETE" ? null : await request.json().catch(() => null);
      if (path.includes("/weekly-groceries")) {
        if (method !== "GET") calls.push({ method, path, body });
        if (method !== "GET" && failure) return failure;
        const id = path.split("/weekly-groceries/")[1];
        if (method === "GET") return json(items);
        if (method === "POST") {
          const created = { ...bread, id: "w3", ingredientId: (body as { ingredientId: string }).ingredientId, name: "Ost" };
          items = [...items, created];
          return json(created, 201);
        }
        if (method === "PUT") {
          const b = body as { quantity: number | null; unitId: string | null };
          items = items.map((i) => (i.id === id ? { ...i, quantity: b.quantity, unitId: b.unitId, unitCode: b.unitId ? "g" : null } : i));
          return json(items.find((i) => i.id === id));
        }
        items = items.filter((i) => i.id !== id);
        return new Response(null, { status: 204 });
      }
      throw new Error(`unexpected ${method} ${path}`);
    }),
  );
});
afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const rows = () => screen.getAllByTestId("weekly-row");

describe("parseAmount", () => {
  it.each([
    ["", null],
    ["  ", null],
    ["2", 2],
    ["1,5", 1.5],
    ["0", "invalid"],
    ["-1", "invalid"],
    ["abc", "invalid"],
  ])("reads %j as %j", (text, expected) => expect(parseAmount(text)).toBe(expected));
});

describe("weekly groceries on the household page", () => {
  it("lists the set with amounts; an adult sees edit, remove and the add form", async () => {
    render(<WeeklyGroceries householdId="h1" canEdit />);

    expect(await screen.findAllByTestId("weekly-row")).toHaveLength(2);
    expect(rows()[0]).toHaveTextContent("Melk");
    expect(rows()[0]).toHaveTextContent("l");
    expect(rows()[1]).toHaveTextContent(t("household.weekly.noAmount"));
    expect(screen.getByRole("button", { name: t("household.weekly.editFor", { name: "Melk" }) })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: t("household.weekly.addTitle") })).toBeInTheDocument();
  });

  it("a child sees the set but no controls to change it", async () => {
    render(<WeeklyGroceries householdId="h1" canEdit={false} />);

    await screen.findAllByTestId("weekly-row");
    expect(screen.queryByRole("button", { name: t("household.weekly.editFor", { name: "Melk" }) })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: t("household.weekly.add") })).not.toBeInTheDocument();
    expect(screen.getByText(t("household.weekly.adultsOnly"))).toBeInTheDocument();
  });

  it("shows a hint when the set is empty", async () => {
    items = [];
    render(<WeeklyGroceries householdId="h1" canEdit />);

    expect(await screen.findByTestId("weekly-empty")).toHaveTextContent(t("household.weekly.empty"));
  });

  it("removes a weekly grocery", async () => {
    render(<WeeklyGroceries householdId="h1" canEdit />);
    await screen.findAllByTestId("weekly-row");

    await userEvent.click(screen.getByRole("button", { name: t("household.weekly.removeFor", { name: "Melk" }) }));

    await waitFor(() => expect(screen.getAllByTestId("weekly-row")).toHaveLength(1));
    expect(calls).toEqual([{ method: "DELETE", path: "/api/households/h1/weekly-groceries/w1", body: null }]);
  });

  it("edits the amount and sends amount and unit together", async () => {
    render(<WeeklyGroceries householdId="h1" canEdit />);
    await screen.findAllByTestId("weekly-row");

    await userEvent.click(screen.getByRole("button", { name: t("household.weekly.editFor", { name: "Brød" }) }));
    const row = rows()[1]!;
    await userEvent.type(within(row).getByLabelText(t("household.weekly.quantity")), "2,5");
    expect(within(row).getByRole("button", { name: t("household.weekly.save") })).toBeDisabled(); // an amount needs a unit
    await userEvent.selectOptions(within(row).getByLabelText(t("household.weekly.unit")), "u-g");
    await userEvent.click(within(row).getByRole("button", { name: t("household.weekly.save") }));

    await waitFor(() => expect(calls).toHaveLength(1));
    expect(calls[0]).toEqual({ method: "PUT", path: "/api/households/h1/weekly-groceries/w2", body: { quantity: 2.5, unitId: "u-g" } });
  });

  it("adds an ingredient found by search, with no amount", async () => {
    render(<WeeklyGroceries householdId="h1" canEdit />);
    await screen.findAllByTestId("weekly-row");

    await userEvent.type(screen.getByLabelText(t("recipe.ingredientSearch")), "ost");
    await userEvent.click(await screen.findByRole("button", { name: /Ost/ }));
    await userEvent.click(screen.getByRole("button", { name: t("household.weekly.add") }));

    await waitFor(() => expect(screen.getAllByTestId("weekly-row")).toHaveLength(3));
    expect(calls).toEqual([
      { method: "POST", path: "/api/households/h1/weekly-groceries", body: { ingredientId: "i-ost", quantity: null, unitId: null } },
    ]);
  });

  it("shows the translated message when the ingredient is already a weekly grocery", async () => {
    failure = json({ status: 409, title: "x", type: "urn:foodplanner:problem:weekly-grocery-exists" }, 409);
    render(<WeeklyGroceries householdId="h1" canEdit />);
    await screen.findAllByTestId("weekly-row");

    await userEvent.type(screen.getByLabelText(t("recipe.ingredientSearch")), "ost");
    await userEvent.click(await screen.findByRole("button", { name: /Ost/ }));
    await userEvent.click(screen.getByRole("button", { name: t("household.weekly.add") }));

    expect(await screen.findByRole("alert")).toHaveTextContent(t("household.weekly.problem.exists"));
  });
});
