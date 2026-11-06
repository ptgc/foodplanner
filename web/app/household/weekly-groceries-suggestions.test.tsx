import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { resetAntiforgeryToken } from "../api/client";
import type { components } from "../api/schema";
import { t } from "../test-i18n";
import { WeeklyGroceries } from "./weekly-groceries";

// T-110: suggestions for regular groceries on the household card.
type Suggestion = components["schemas"]["GrocerySuggestionResponse"];

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": status >= 400 ? "application/problem+json" : "application/json" },
  });
const problem = (type: string, status: number) => json({ type: `urn:foodplanner:problem:${type}`, title: "x", status }, status);

const coffee: Suggestion = { label: "Kaffe", frequency: "EveryTwoWeeks", weeksSeen: 6, weeksLooked: 12 };
const eggs: Suggestion = { label: "Egg", frequency: "Weekly", weeksSeen: 9, weeksLooked: 12 };

type Call = { method: string; path: string; body: unknown };
let suggestions: Suggestion[];
let calls: Call[];
let gets: string[];
let suggestionsGet: () => Response;
let createStatus: () => Response | null;
let dismissResponse: () => Response;

beforeEach(() => {
  resetAntiforgeryToken();
  suggestions = [coffee, eggs];
  calls = [];
  gets = [];
  suggestionsGet = () => json(suggestions);
  createStatus = () => null;
  dismissResponse = () => new Response(null, { status: 204 });
  vi.stubGlobal(
    "fetch",
    vi.fn(async (request: Request) => {
      const path = new URL(request.url).pathname;
      const method = request.method;
      if (path === "/api/auth/antiforgery") return json({ token: "t" });
      if (path === "/api/units") return json([]);
      if (path.endsWith("/ingredients") && method === "GET") {
        return json([{ id: "i-filter", name: "Filterkaffe", isStaple: false, dimension: "Mass", isGlobal: true, section: "Other" }]);
      }
      const body = method === "GET" ? null : await request.json().catch(() => null);
      if (method === "GET") gets.push(path);
      else calls.push({ method, path, body });
      if (path.endsWith("/suggestions") && method === "GET") return suggestionsGet();
      if (path.endsWith("/suggestions/dismiss")) {
        const res = dismissResponse();
        if (res.ok) suggestions = suggestions.filter((s) => s.label !== (body as { label: string }).label);
        return res;
      }
      if (path.endsWith("/weekly-groceries") && method === "GET") return json([]);
      if (path.endsWith("/weekly-groceries") && method === "POST") {
        return createStatus() ?? json({ id: "w1" }, 201);
      }
      throw new Error(`unexpected ${method} ${path}`);
    }),
  );
});
afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const rowOf = (label: string) => screen.getAllByTestId("suggestion-row").find((r) => r.textContent?.includes(label))!;
const accept = async (label: string) => {
  await userEvent.click(await screen.findByRole("button", { name: t("household.suggest.addFor", { label }) }));
};

describe("regular grocery suggestions", () => {
  it("shows each suggestion with the reason and named buttons", async () => {
    render(<WeeklyGroceries householdId="h1" canEdit />);

    expect(await screen.findByRole("heading", { name: t("household.suggest.title") })).toBeInTheDocument();
    expect(screen.getAllByTestId("suggestion-row")).toHaveLength(2);
    expect(rowOf("Kaffe")).toHaveTextContent(
      t("household.suggest.reason", { seen: 6, looked: 12, frequency: t("household.weekly.freq.EveryTwoWeeks") }),
    );
    expect(within(rowOf("Egg")).getByRole("button", { name: t("household.suggest.addFor", { label: "Egg" }) })).toBeInTheDocument();
    expect(within(rowOf("Egg")).getByRole("button", { name: t("household.suggest.dismissFor", { label: "Egg" }) })).toBeInTheDocument();
  });

  it("hides the section when there are no suggestions", async () => {
    suggestions = [];
    render(<WeeklyGroceries householdId="h1" canEdit />);

    await screen.findByTestId("weekly-empty");
    await waitFor(() => expect(gets.some((g) => g.endsWith("/suggestions"))).toBe(true));
    expect(screen.queryByTestId("weekly-suggestions")).not.toBeInTheDocument();
  });

  it("hides the section quietly when the read fails", async () => {
    suggestionsGet = () => problem("forbidden", 403);
    render(<WeeklyGroceries householdId="h1" canEdit />);

    await screen.findByTestId("weekly-empty");
    await waitFor(() => expect(gets.some((g) => g.endsWith("/suggestions"))).toBe(true));
    expect(screen.queryByTestId("weekly-suggestions")).not.toBeInTheDocument();
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });

  it("a child never reads the suggestions", async () => {
    render(<WeeklyGroceries householdId="h1" canEdit={false} />);

    await screen.findByTestId("weekly-empty");
    expect(gets.filter((g) => g.endsWith("/suggestions"))).toEqual([]);
    expect(screen.queryByTestId("weekly-suggestions")).not.toBeInTheDocument();
  });

  it("accepting opens the picker prefilled with the label and the suggested frequency, then adds, dismisses the label and reloads", async () => {
    render(<WeeklyGroceries householdId="h1" canEdit />);
    await accept("Kaffe");

    const row = rowOf("Kaffe");
    expect(within(row).getByLabelText(t("recipe.ingredientSearch"))).toHaveValue("Kaffe");
    expect(within(row).getByLabelText(t("household.weekly.frequency"))).toHaveValue("EveryTwoWeeks");
    await userEvent.selectOptions(within(row).getByLabelText(t("household.weekly.frequency")), "Monthly");
    await userEvent.click(await within(row).findByRole("button", { name: /Filterkaffe/ }));
    const getsBefore = gets.filter((g) => g.endsWith("/suggestions")).length;
    await userEvent.click(within(row).getByRole("button", { name: t("household.weekly.add") }));

    await waitFor(() => expect(screen.getAllByTestId("suggestion-row")).toHaveLength(1));
    expect(calls).toEqual([
      { method: "POST", path: "/api/households/h1/weekly-groceries", body: { ingredientId: "i-filter", quantity: null, unitId: null, frequency: "Monthly" } },
      { method: "POST", path: "/api/households/h1/weekly-groceries/suggestions/dismiss", body: { label: "Kaffe" } },
    ]);
    expect(gets.filter((g) => g.endsWith("/suggestions")).length).toBeGreaterThan(getsBefore);
    expect(screen.getAllByTestId("suggestion-row")).toHaveLength(1);
    expect(rowOf("Egg")).toBeInTheDocument();
  });

  it("says plainly when the ingredient is already a regular grocery", async () => {
    createStatus = () => problem("weekly-grocery-exists", 409);
    render(<WeeklyGroceries householdId="h1" canEdit />);
    await accept("Kaffe");

    const row = rowOf("Kaffe");
    await userEvent.click(await within(row).findByRole("button", { name: /Filterkaffe/ }));
    await userEvent.click(within(row).getByRole("button", { name: t("household.weekly.add") }));

    expect(await within(row).findByRole("alert")).toHaveTextContent(t("household.weekly.problem.exists"));
    expect(calls.filter((c) => c.path.endsWith("/dismiss"))).toEqual([]); // nothing was added, so nothing is dismissed
    expect(screen.getAllByTestId("suggestion-row")).toHaveLength(2);
  });

  it("cancelling the add form returns to the row", async () => {
    render(<WeeklyGroceries householdId="h1" canEdit />);
    await accept("Egg");

    await userEvent.click(within(rowOf("Egg")).getByRole("button", { name: t("household.weekly.cancel") }));

    expect(screen.getByRole("button", { name: t("household.suggest.addFor", { label: "Egg" }) })).toBeInTheDocument();
    expect(calls).toEqual([]);
  });

  it("Not now removes the row at once and sends the dismissal", async () => {
    let release!: () => void;
    const gate = new Promise<void>((resolve) => (release = resolve));
    dismissResponse = () => new Response(null, { status: 204 });
    const inner = globalThis.fetch;
    vi.stubGlobal("fetch", async (request: Request) => {
      if (new URL(request.url).pathname.endsWith("/dismiss")) await gate;
      return inner(request);
    });
    render(<WeeklyGroceries householdId="h1" canEdit />);
    await screen.findAllByTestId("suggestion-row");

    await userEvent.click(screen.getByRole("button", { name: t("household.suggest.dismissFor", { label: "Egg" }) }));

    expect(screen.getAllByTestId("suggestion-row")).toHaveLength(1); // gone before the server answered
    release();
    await waitFor(() => expect(calls).toEqual([{ method: "POST", path: "/api/households/h1/weekly-groceries/suggestions/dismiss", body: { label: "Egg" } }]));
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });

  it("Not now puts the row back with a message when the dismissal fails", async () => {
    dismissResponse = () => problem("dismissed-suggestion-limit", 409);
    render(<WeeklyGroceries householdId="h1" canEdit />);
    await screen.findAllByTestId("suggestion-row");

    await userEvent.click(screen.getByRole("button", { name: t("household.suggest.dismissFor", { label: "Egg" }) }));

    expect(await screen.findByRole("alert")).toHaveTextContent(t("household.suggest.problem.limit"));
    expect(screen.getAllByTestId("suggestion-row")).toHaveLength(2);
    expect(rowOf("Egg")).toBeInTheDocument();
  });
});
