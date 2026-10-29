import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { resetAntiforgeryToken } from "../api/client";
import type { components } from "../api/schema";
import { searchDelayMs } from "../recipes/ingredient-picker";
import { SessionProvider } from "../session";
import { t } from "../test-i18n";
import Pantry, { dateFromExpiry, expiryFromDate } from "./pantry";

type Entry = components["schemas"]["PantryItemResponse"];
type Role = components["schemas"]["HouseholdRole"];

const me = { userId: "11111111-1111-1111-1111-111111111111", email: "owner@example.test" };
const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": status >= 400 ? "application/problem+json" : "application/json" },
  });

const entry = (over: Partial<Entry> & { ingredientId: string; name: string }): Entry => ({
  confirmedAt: "2026-10-25T10:00:00Z",
  confirmedByUserId: me.userId,
  expiresAt: null,
  quantity: null,
  unitId: null,
  unitCode: null,
  unitName: null,
  isExpired: false,
  ...over,
});

const units = [
  { id: "u-g", code: "g", name: "Gram", dimension: "Mass", factorToBase: 1 },
  { id: "u-l", code: "l", name: "Liter", dimension: "Volume", factorToBase: 1000 },
];

type Call = { method: string; path: string; query: string; body: unknown };
let role: Role;
let entries: Entry[];
let calls: Call[];

beforeEach(() => {
  resetAntiforgeryToken();
  role = "Owner";
  calls = [];
  entries = [
    entry({ ingredientId: "i-ost", name: "Ost", quantity: 300, unitId: "u-g", unitCode: "g", unitName: "Gram" }),
    entry({ ingredientId: "i-tortilla", name: "Tortillalefser" }),
    entry({ ingredientId: "i-melk", name: "Melk", quantity: 1, unitId: "u-l", unitCode: "l", unitName: "Liter", expiresAt: "2026-10-20T21:59:59Z", isExpired: true }),
  ];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (request: Request) => {
      const url = new URL(request.url);
      const path = url.pathname;
      const method = request.method;
      if (path === "/api/auth/me") return json(me);
      if (path === "/api/auth/antiforgery") return json({ token: "t" });
      if (path === "/api/households") return json([{ id: "h1", name: "Home", role }]);
      if (path === "/api/units") return json(units);
      if (path === "/api/households/h1/ingredients" && method === "GET") {
        return json([{ id: "i-brod", name: "Brød", isStaple: false, dimension: "Count", isGlobal: true, section: "DryGoods" }]);
      }
      const body = method === "GET" || method === "DELETE" ? null : await request.json().catch(() => null);
      const pantry = path.match(/^\/api\/households\/h1\/pantry(?:\/([^/]+))?$/);
      if (pantry) {
        calls.push({ method, path, query: url.search, body });
        const id = pantry[1];
        if (method === "GET") return json(entries);
        if (method === "PUT") {
          const b = body as { quantity: number | null; unitId: string | null; expiresAt: string | null };
          const saved = entry({
            ingredientId: id!,
            name: entries.find((e) => e.ingredientId === id)?.name ?? "Brød",
            quantity: b.quantity,
            unitId: b.unitId,
            unitCode: b.unitId ? "g" : null,
            expiresAt: b.expiresAt,
          });
          entries = [...entries.filter((e) => e.ingredientId !== id), saved];
          return json(saved);
        }
        entries = id ? entries.filter((e) => e.ingredientId !== id) : [];
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

function renderPage() {
  return render(
    <MemoryRouter initialEntries={["/pantry"]}>
      <SessionProvider>
        <Pantry />
      </SessionProvider>
    </MemoryRouter>,
  );
}

const rows = () => screen.getAllByTestId("pantry-row");
const puts = () => calls.filter((c) => c.method === "PUT");

describe("expiry dates", () => {
  it("keeps the chosen day: the end of that local day, and back", () => {
    const iso = expiryFromDate("2031-03-04")!;
    expect(dateFromExpiry(iso)).toBe("2031-03-04");
    expect(new Date(iso).getHours()).toBe(23);
    expect(expiryFromDate("")).toBeNull();
    expect(dateFromExpiry(null)).toBe("");
  });
});

describe("pantry screen", () => {
  it("lists everything with amount and expiry, including an expired entry, and asks for expired ones too", async () => {
    renderPage();
    await screen.findByTestId("pantry-list");

    expect(calls[0]).toMatchObject({ method: "GET", query: "?includeExpired=true" });
    expect(rows().map((r) => within(r).getByTestId("pantry-amount").textContent)).toEqual(["300 g", t("pantry.noAmount"), "1 l"]);
    expect(rows()[2]).toHaveTextContent(t("pantry.expired", { date: "" }).replace(/\s*$/, ""));
  });

  it("adds an ingredient with an amount, a unit and an expiry", async () => {
    const user = userEvent.setup();
    renderPage();
    await screen.findByTestId("pantry-list");

    await user.type(screen.getByLabelText(t("recipe.ingredientSearch")), "br");
    await user.click(await screen.findByRole("button", { name: "Brød" }, { timeout: searchDelayMs + 2000 }));
    await user.type(screen.getByLabelText(t("pantry.amount")), "2,5");
    await user.selectOptions(screen.getByLabelText(t("pantry.unit")), "u-g");
    await user.type(screen.getByLabelText(t("pantry.expiry")), "2099-01-31");
    await user.click(screen.getByRole("button", { name: t("pantry.add") }));

    await waitFor(() => expect(puts()).toHaveLength(1));
    expect(puts()[0]).toMatchObject({ path: "/api/households/h1/pantry/i-brod" });
    expect(puts()[0]!.body).toMatchObject({ quantity: 2.5, unitId: "u-g" });
    expect(dateFromExpiry((puts()[0]!.body as { expiresAt: string }).expiresAt)).toBe("2099-01-31");
  });

  it("does not allow an amount without a unit", async () => {
    const user = userEvent.setup();
    renderPage();
    await screen.findByTestId("pantry-list");
    await user.click(screen.getByRole("button", { name: t("pantry.editFor", { name: "Ost" }) }));
    const form = rows()[0]!;

    await user.selectOptions(within(form).getByLabelText(t("pantry.unit")), "");

    expect(within(form).getByRole("button", { name: t("pantry.save") })).toBeDisabled();
  });

  it("edits an entry: the request replaces amount and expiry, and clearing the amount sends none", async () => {
    const user = userEvent.setup();
    renderPage();
    await screen.findByTestId("pantry-list");

    await user.click(screen.getByRole("button", { name: t("pantry.editFor", { name: "Ost" }) }));
    const amount = within(rows()[0]!).getByLabelText(t("pantry.amount"));
    expect(amount).toHaveValue("300");
    await user.clear(amount);
    await user.type(amount, "100");
    await user.click(within(rows()[0]!).getByRole("button", { name: t("pantry.save") }));

    await waitFor(() => expect(puts()).toHaveLength(1));
    expect(puts()[0]).toMatchObject({ path: "/api/households/h1/pantry/i-ost", body: { quantity: 100, unitId: "u-g", expiresAt: null } });
    await waitFor(() => expect(screen.getAllByTestId("pantry-amount").map((a) => a.textContent)).toContain("100 g"));
  });

  it("edits an expired entry without sending the old date again", async () => {
    const user = userEvent.setup();
    renderPage();
    await screen.findByTestId("pantry-list");

    await user.click(screen.getByRole("button", { name: t("pantry.editFor", { name: "Melk" }) }));
    expect(within(rows()[2]!).getByLabelText(t("pantry.expiry"))).toHaveValue("");
    await user.click(within(rows()[2]!).getByRole("button", { name: t("pantry.save") }));

    await waitFor(() => expect(puts()).toHaveLength(1));
    expect((puts()[0]!.body as { expiresAt: string | null }).expiresAt).toBeNull();
  });

  it("removes one entry", async () => {
    const user = userEvent.setup();
    renderPage();
    await screen.findByTestId("pantry-list");

    await user.click(screen.getByRole("button", { name: t("pantry.removeFor", { name: "Ost" }) }));

    await waitFor(() => expect(calls.some((c) => c.method === "DELETE" && c.path.endsWith("/pantry/i-ost"))).toBe(true));
    await waitFor(() => expect(rows()).toHaveLength(2));
  });

  it("an owner clears the whole pantry after confirming", async () => {
    const user = userEvent.setup();
    renderPage();
    await screen.findByTestId("pantry-list");

    await user.click(screen.getByRole("button", { name: t("pantry.clear") }));
    const dialog = await screen.findByRole("dialog");
    await user.click(within(dialog).getByRole("button", { name: t("pantry.clearConfirm") }));

    await waitFor(() => expect(calls.some((c) => c.method === "DELETE" && c.path === "/api/households/h1/pantry")).toBe(true));
    await screen.findByTestId("pantry-empty");
  });

  it("a child can add and edit but is told that only adults clear the pantry", async () => {
    role = "Child";
    renderPage();
    await screen.findByTestId("pantry-list");

    expect(screen.queryByRole("button", { name: t("pantry.clear") })).toBeNull();
    expect(screen.getByText(t("pantry.adultsOnlyClear"))).toBeInTheDocument();
    expect(screen.getByRole("button", { name: t("pantry.editFor", { name: "Ost" }) })).toBeInTheDocument();
  });

  it("shows the empty state", async () => {
    entries = [];
    renderPage();

    expect(await screen.findByTestId("pantry-empty")).toHaveTextContent(t("pantry.empty"));
  });
});
