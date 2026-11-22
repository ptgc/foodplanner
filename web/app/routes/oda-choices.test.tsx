import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { resetAntiforgeryToken } from "../api/client";
import type { components } from "../api/schema";
import { SessionProvider } from "../session";
import { t } from "../test-i18n";
import OdaChoices from "./oda-choices";

type Row = components["schemas"]["OdaPreferenceResponse"];
type Role = components["schemas"]["HouseholdRole"];

const me = { userId: "11111111-1111-1111-1111-111111111111", email: "owner@example.test" };
const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "content-type": status >= 400 ? "application/problem+json" : "application/json" } });

let role: Role;
let rows: Row[];
let calls: { method: string; path: string }[];
let deleteStatus: number;

beforeEach(() => {
  resetAntiforgeryToken();
  role = "Adult";
  deleteStatus = 204;
  calls = [];
  rows = [
    { ingredientId: "i-egg", ingredientName: "Egg", setAt: "2026-11-20T10:00:00Z" },
    { ingredientId: "i-rice", ingredientName: "Rice", setAt: "2026-11-21T10:00:00Z" },
  ];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (request: Request) => {
      const url = new URL(request.url);
      const path = url.pathname;
      if (path === "/api/auth/me") return json(me);
      if (path === "/api/auth/antiforgery") return json({ token: "t" });
      if (path === "/api/households") return json([{ id: "h1", name: "Home", role }]);
      const match = path.match(/^\/api\/households\/h1\/oda-preferences(?:\/([^/]+))?$/);
      if (match) {
        calls.push({ method: request.method, path });
        if (request.method === "GET") return json(rows);
        if (deleteStatus !== 204) return json({ type: "urn:foodplanner:problem:forbidden", title: "x", status: deleteStatus }, deleteStatus);
        rows = rows.filter((r) => r.ingredientId !== match[1]);
        return new Response(null, { status: 204 });
      }
      throw new Error(`unexpected ${request.method} ${path}`);
    }),
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  window.localStorage.clear();
  window.sessionStorage.clear();
});

function renderPage() {
  return render(
    <MemoryRouter initialEntries={["/shopping/oda-choices"]}>
      <SessionProvider>
        <OdaChoices />
      </SessionProvider>
    </MemoryRouter>,
  );
}

describe("the Oda choices page (T-052: Choices_page_web)", () => {
  it("lists one row per ingredient with the fixed text and the date, and no product name or id", async () => {
    renderPage();

    const list = await screen.findByTestId("oda-choices-list");

    const items = within(list).getAllByTestId("oda-choice-row");
    expect(items).toHaveLength(2);
    expect(items[0]).toHaveTextContent("Egg");
    expect(items[0]).toHaveTextContent(t("oda.result.choice"));
    expect(items[0]).toHaveTextContent(t("oda.choices.setOn", { date: "" }).replace(/\s*$/, ""));
    expect(list.textContent).not.toMatch(/\d{5,}/); // no product id; the date is written out
    expect(calls.map((c) => c.method)).toEqual(["GET"]);
  });

  it("clears a row with a DELETE and confirms it", async () => {
    const user = userEvent.setup();
    renderPage();
    await screen.findByTestId("oda-choices-list");

    await user.click(screen.getByRole("button", { name: t("oda.choices.clearFor", { name: "Egg" }) }));

    await waitFor(() => expect(screen.getAllByTestId("oda-choice-row")).toHaveLength(1));
    expect(calls).toContainEqual({ method: "DELETE", path: "/api/households/h1/oda-preferences/i-egg" });
    expect(screen.getByTestId("oda-choices-cleared")).toHaveTextContent(t("oda.choices.cleared"));
    expect(screen.queryByText("Egg")).toBeNull();
  });

  it("shows an error and keeps the row when clearing fails", async () => {
    const user = userEvent.setup();
    deleteStatus = 403;
    renderPage();
    await screen.findByTestId("oda-choices-list");

    await user.click(screen.getByRole("button", { name: t("oda.choices.clearFor", { name: "Egg" }) }));

    expect(await screen.findByTestId("oda-choices-error")).toHaveTextContent(t("problem.forbidden"));
    expect(screen.getAllByTestId("oda-choice-row")).toHaveLength(2);
  });

  it("explains how to choose when there is nothing yet", async () => {
    rows = [];
    renderPage();

    expect(await screen.findByTestId("oda-choices-empty")).toHaveTextContent(t("oda.choices.empty"));
    expect(screen.queryByTestId("oda-choices-list")).toBeNull();
  });

  it("keeps showing what was loaded when offline, with the buttons disabled", async () => {
    vi.spyOn(navigator, "onLine", "get").mockReturnValue(false);
    renderPage();
    await screen.findByTestId("oda-choices-list");

    expect(screen.getByTestId("oda-choices-offline")).toHaveTextContent(t("oda.choices.offline"));
    for (const button of screen.getAllByRole("button")) expect(button).toBeDisabled();
  });

  it("shows a Child only the note and never asks for the list", async () => {
    role = "Child";
    renderPage();

    expect(await screen.findByTestId("oda-choices-adults-only")).toHaveTextContent(t("oda.choices.adultsOnly"));
    expect(screen.queryByTestId("oda-choices-list")).toBeNull();
    expect(calls).toHaveLength(0);
  });
});
