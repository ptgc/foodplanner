import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { resetAntiforgeryToken } from "../api/client";
import type { components } from "../api/schema";
import { translate } from "../i18n";
import { t } from "../test-i18n";
import { addedLine } from "./oda-fixtures";
import { OdaResult } from "./oda-result";

type Cart = components["schemas"]["OdaCartResponse"];

const pending = { householdId: "h1", weekStart: "2026-10-19" };
const tomato = "11111111-1111-1111-1111-111111111111";
const rice = "22222222-2222-2222-2222-222222222222";

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "content-type": status >= 400 ? "application/problem+json" : "application/json" } });

const alternatives = [
  { productId: 2001, productName: "Cherrytomater 250 g", isAdded: true, isPreferred: false },
  { productId: 2002, productName: "Favoritt cherry <b>pack</b>", isAdded: false, isPreferred: false },
  { productId: 2003, productName: "Tomat Tredje", isAdded: false, isPreferred: false },
];

function cart(lines: Cart["added"]): Cart {
  return {
    status: "done",
    cartUrl: "https://oda.example/cart",
    added: lines,
    notAdded: [],
    cartMessages: [],
    connectionClosed: true,
    connectionMessage: null,
    usuallyBought: { status: "used", count: 0 },
  };
}

const tomatoLine = (over: Partial<Cart["added"][number]> = {}) =>
  addedLine({ itemId: "i1", name: "Cherry tomatoes", productName: "Cherrytomater 250 g", ingredientId: tomato, productId: 2001, alternatives, ...over });

type Call = { method: string; path: string; body: unknown };
let calls: Call[];
let answer: (call: Call) => Response;

beforeEach(() => {
  resetAntiforgeryToken();
  calls = [];
  answer = (c) => (c.method === "DELETE" ? new Response(null, { status: 204 }) : json({ ingredientId: tomato, ingredientName: "Cherry tomatoes", setAt: "2026-11-22T10:00:00Z" }));
  vi.stubGlobal(
    "fetch",
    vi.fn(async (request: Request) => {
      const url = new URL(request.url);
      if (url.pathname === "/api/auth/antiforgery") return json({ token: "t" });
      const body = request.method === "GET" || request.method === "DELETE" ? null : await request.json().catch(() => null);
      const call = { method: request.method, path: url.pathname, body };
      calls.push(call);
      return answer(call);
    }),
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  window.localStorage.clear();
  window.sessionStorage.clear();
});

function show(result: Cart) {
  return render(
    <MemoryRouter>
      <OdaResult result={{ kind: "result", cart: result }} pending={pending} />
    </MemoryRouter>,
  );
}

const open = async (user: ReturnType<typeof userEvent.setup>) => user.click(await screen.findByRole("button", { name: t("oda.pick.open") }));

describe("the picker on the Oda result (T-052: Picker_web)", () => {
  it("shows for a line with two or more alternatives, not for a manual item or a line with one and no choice", async () => {
    show(
      cart([
        tomatoLine(),
        addedLine({ itemId: "i2", name: "Smør", productName: "Smør", ingredientId: null, alternatives: [] }),
        addedLine({ itemId: "i3", name: "Rice", productName: "Rice", ingredientId: rice, alternatives: [{ productId: 1, productName: "Rice", isAdded: true, isPreferred: false }] }),
      ]),
    );

    expect(await screen.findAllByRole("button", { name: t("oda.pick.open") })).toHaveLength(1);
    expect(screen.getByText("Smør", { exact: false })).toBeInTheDocument();
  });

  it("shows for a line with a choice even with fewer than two alternatives", async () => {
    show(cart([tomatoLine({ hasPreference: true, source: "preferred", alternatives: [{ ...alternatives[0]!, isPreferred: true }] })]));

    expect(await screen.findByRole("button", { name: t("oda.pick.open") })).toBeInTheDocument();
    expect(screen.getByTestId("oda-choice")).toHaveTextContent(t("oda.result.choice"));
  });

  it("opens an inline list with the cart note, product names as plain text, and the current product marked", async () => {
    const user = userEvent.setup();
    show(cart([tomatoLine()]));

    await open(user);

    const rows = screen.getAllByTestId("oda-alternative");
    expect(rows).toHaveLength(3);
    expect(rows[0]).toHaveTextContent(t("oda.pick.inCart"));
    expect(rows[1]).not.toHaveTextContent(t("oda.pick.inCart"));
    expect(rows[1]).toHaveTextContent("Favoritt cherry <b>pack</b>"); // text, not markup
    expect(document.querySelector("b")).toBeNull();
    expect(screen.getByText(t("oda.pick.note"))).toBeInTheDocument();
  });

  it("saves a choice with a PUT of that alternative's id and shows the saved state and the line label", async () => {
    const user = userEvent.setup();
    show(cart([tomatoLine()]));
    await open(user);

    await user.click(within(screen.getAllByTestId("oda-alternative")[2]!).getByRole("button", { name: t("oda.pick.useFor", { product: "Tomat Tredje" }) }));

    await waitFor(() => expect(screen.getByText(t("oda.pick.saved"))).toBeInTheDocument());
    expect(calls).toEqual([{ method: "PUT", path: `/api/households/h1/oda-preferences/${tomato}`, body: { odaProductId: 2003 } }]);
    expect(screen.getByTestId("oda-choice")).toHaveTextContent(t("oda.result.choice"));
    expect(screen.getByRole("button", { name: t("oda.pick.removeFor", { name: "Cherry tomatoes" }) })).toBeInTheDocument();
    expect(screen.getAllByTestId("oda-alternative")).toHaveLength(3); // the list stays open
  });

  it("removes a choice with a DELETE", async () => {
    const user = userEvent.setup();
    show(cart([tomatoLine({ hasPreference: true, source: "preferred", productId: 2002, alternatives: alternatives.map((a) => ({ ...a, isPreferred: a.productId === 2002 })) })]));

    await user.click(await screen.findByRole("button", { name: t("oda.pick.removeFor", { name: "Cherry tomatoes" }) }));

    await waitFor(() => expect(screen.getByTestId("oda-removed")).toBeInTheDocument());
    expect(calls).toEqual([{ method: "DELETE", path: `/api/households/h1/oda-preferences/${tomato}`, body: null }]);
    expect(screen.queryByTestId("oda-choice")).toBeNull();
    expect(screen.queryByRole("button", { name: t("oda.pick.removeFor", { name: "Cherry tomatoes" }) })).toBeNull();
  });

  it.each([
    [403, "urn:foodplanner:problem:forbidden", "problem.forbidden"],
    [409, "urn:foodplanner:problem:oda-preference-limit", "oda.pick.problem.limit"],
    [409, "urn:foodplanner:problem:oda-preference-conflict", "oda.pick.problem.conflict"],
    [429, "urn:foodplanner:problem:rate-limited", "problem.rateLimited"],
  ] as const)("shows an inline error for %s and keeps the list open", async (status, type, key) => {
    const user = userEvent.setup();
    answer = () => json({ type, title: "x", status }, status);
    show(cart([tomatoLine()]));
    await open(user);

    await user.click(within(screen.getAllByTestId("oda-alternative")[1]!).getByRole("button"));

    expect(await screen.findByTestId("oda-pick-error")).toHaveTextContent(t(key));
    expect(screen.getAllByTestId("oda-alternative")).toHaveLength(3);
    expect(screen.queryByText(t("oda.pick.saved"))).toBeNull();
    expect(screen.queryByTestId("oda-choice")).toBeNull();
  });

  it("shows an inline error when the network fails", async () => {
    const user = userEvent.setup();
    answer = () => {
      throw new TypeError("network");
    };
    show(cart([tomatoLine()]));
    await open(user);

    await user.click(within(screen.getAllByTestId("oda-alternative")[1]!).getByRole("button"));

    expect(await screen.findByTestId("oda-pick-error")).toHaveTextContent(t("problem.unreachable"));
  });

  it("disables the buttons offline and says so", async () => {
    const user = userEvent.setup();
    vi.spyOn(navigator, "onLine", "get").mockReturnValue(false);
    show(cart([tomatoLine({ hasPreference: true })]));
    await open(user);

    for (const row of screen.getAllByTestId("oda-alternative")) {
      expect(within(row).getByRole("button")).toBeDisabled();
    }
    expect(screen.getByRole("button", { name: t("oda.pick.removeFor", { name: "Cherry tomatoes" }) })).toBeDisabled();
    expect(screen.getByText(t("oda.pick.offline"))).toBeInTheDocument();
    expect(calls).toHaveLength(0);
  });

  it("labels the line by where the product came from and why a choice was not used", () => {
    show(
      cart([
        tomatoLine({ itemId: "a", name: "A", source: "preferred", hasPreference: true }),
        tomatoLine({ itemId: "b", name: "B", source: "usually-bought" }),
        tomatoLine({ itemId: "c", name: "C", hasPreference: true, preferenceProblem: "unavailable" }),
        tomatoLine({ itemId: "d", name: "D", hasPreference: true, preferenceProblem: "not-found" }),
      ]),
    );

    expect(screen.getAllByTestId("oda-choice")).toHaveLength(1);
    expect(screen.getAllByTestId("oda-usual")).toHaveLength(1);
    expect(screen.getAllByTestId("oda-choice-problem").map((p) => p.textContent)).toEqual([
      t("oda.result.problem.unavailable"),
      t("oda.result.problem.not-found"),
    ]);
  });

  it("is not offered on the lines of a failed fill, and writes nothing to browser storage", async () => {
    const user = userEvent.setup();
    render(
      <MemoryRouter>
        <OdaResult
          result={{ kind: "failed", reason: "unavailable", added: [tomatoLine({ hasPreference: true })], connectionClosed: true, offerText: false }}
          pending={pending}
        />
      </MemoryRouter>,
    );
    expect(screen.queryByRole("button", { name: t("oda.pick.open") })).toBeNull();

    show(cart([tomatoLine()]));
    await open(user);
    await user.click(within(screen.getAllByTestId("oda-alternative")[1]!).getByRole("button"));
    await screen.findByText(t("oda.pick.saved"));

    expect(window.localStorage.length).toBe(0);
    expect(window.sessionStorage.length).toBe(0);
  });

  it("has every picker text in Norwegian and in English", () => {
    const keys = [
      "oda.result.choice",
      "oda.result.problem.unavailable",
      "oda.result.problem.not-found",
      "oda.pick.open",
      "oda.pick.use",
      "oda.pick.saved",
      "oda.pick.remove",
      "oda.pick.note",
      "oda.choices.link",
      "oda.choices.clear",
    ] as const;
    for (const key of keys) {
      expect(translate("nb", key)).not.toBe(translate("en", key));
      expect(translate("nb", key)).not.toBe(key);
    }
    expect(translate("nb", "oda.pick.open")).toBe("Velg et annet produkt");
    expect(translate("en", "oda.pick.use")).toBe("Use this next time");
  });
});
