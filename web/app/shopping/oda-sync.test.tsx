import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { StrictMode } from "react";
import { MemoryRouter } from "react-router";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { resetAntiforgeryToken } from "../api/client";
import type { components } from "../api/schema";
import OdaCallback, { resetOdaCallbackForTests } from "../routes/oda-callback";
import OdaChoices from "../routes/oda-choices";
import { SessionProvider } from "../session";
import { t } from "../test-i18n";
import { navigation, rememberSyncPending } from "./oda";

type Review = components["schemas"]["OdaSyncReviewResponse"];
type Row = Review["rows"][number];
type Role = components["schemas"]["HouseholdRole"];

const me = { userId: "11111111-1111-1111-1111-111111111111", email: "owner@example.test" };
const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "content-type": status >= 400 ? "application/problem+json" : "application/json" } });
const problem = (type: string, status: number, extra: Record<string, unknown> = {}) => json({ type: `urn:foodplanner:problem:${type}`, title: "x", status, ...extra }, status);

const secret = "SECRET-CODE-123";

const option = (productId: number, productName: string, over: Partial<Row["options"][number]> = {}) => ({ productId, productName, isUsual: false, isCurrent: false, ...over });

const rice: Row = {
  ingredientId: "i-rice", ingredientName: "Rice", category: "usually-bought", reason: null, hasExistingChoice: false, selectedProductId: 1004, preticked: true,
  options: [option(1001, "Jasmine rice 1 kg"), option(1004, "Basmati rice 1 kg", { isUsual: true })],
};
const egg: Row = {
  ingredientId: "i-egg", ingredientName: "Egg", category: "suggestion", reason: null, hasExistingChoice: false, selectedProductId: 1003, preticked: false,
  options: [option(1003, "Eggs 12 pack"), option(1007, "Organic eggs 6 pack")],
};
const pasta: Row = {
  ingredientId: "i-pasta", ingredientName: "Pasta", category: "suggestion", reason: null, hasExistingChoice: true, selectedProductId: 3001, preticked: false,
  options: [option(3001, "Penne 500 g")],
};
const milk: Row = {
  ingredientId: "i-milk", ingredientName: "Milk", category: "current", reason: null, hasExistingChoice: true, selectedProductId: null, preticked: false,
  options: [option(2001, "Lettmelk 1 l", { isCurrent: true }), option(2002, "Helmelk 1 l")],
};
const coffee: Row = {
  ingredientId: "i-coffee", ingredientName: "Coffee filters", category: "not-found", reason: "no-match", hasExistingChoice: false, selectedProductId: null, preticked: false, options: [],
};

const review = (over: Partial<Review> = {}): Review => ({
  status: "done", stopped: null, usuallyBought: "used", rows: [rice, egg, pasta, milk, coffee], notCheckedCount: 0, connectionClosed: true, connectionMessage: null, ...over,
});

let role: Role;
let startAnswer: () => Response;
let reviewAnswer: () => Response;
let saveAnswer: (body: unknown) => Response;
let calls: { method: string; path: string; body: unknown }[];

function install() {
  vi.stubGlobal(
    "fetch",
    vi.fn(async (request: Request) => {
      const path = new URL(request.url).pathname;
      if (path === "/api/auth/me") return json(me);
      if (path === "/api/auth/antiforgery") return json({ token: "tok" });
      if (path === "/api/households") return json([{ id: "h1", name: "Home", role }]);
      const body = request.method === "GET" || request.method === "DELETE" ? null : await request.json();
      calls.push({ method: request.method, path, body });
      if (path === "/api/households/h1/oda-sync/start") return startAnswer();
      if (path === "/api/households/h1/oda-sync/review") return reviewAnswer();
      if (path === "/api/households/h1/oda-preferences" && request.method === "PUT") return saveAnswer(body);
      if (path === "/api/households/h1/oda-preferences" && request.method === "GET") return json([]);
      throw new Error(`unexpected ${request.method} ${path}`);
    }),
  );
}

beforeEach(() => {
  resetAntiforgeryToken();
  resetOdaCallbackForTests();
  window.localStorage.clear();
  window.sessionStorage.clear();
  role = "Adult";
  calls = [];
  startAnswer = () => json({ authorizeUrl: "https://oda.example/o/authorize/?state=s1", ingredientCount: 120, notCheckedCount: 0 });
  reviewAnswer = () => json(review());
  saveAnswer = (body) => json({ created: (body as { items: unknown[] }).items.length, replaced: 0, unchanged: 0, skippedExisting: [] });
  install();
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  window.history.replaceState(null, "", "/");
});

function renderChoices() {
  return render(
    <MemoryRouter initialEntries={["/shopping/oda-choices"]}>
      <SessionProvider>
        <OdaChoices />
      </SessionProvider>
    </MemoryRouter>,
  );
}

describe("the Sync with Oda button and confirmation", () => {
  it("shows the count and two unticked boxes, counts again when a box is ticked, and leaves for Oda with the options remembered", async () => {
    const user = userEvent.setup();
    const go = vi.spyOn(navigation, "go").mockImplementation(() => undefined);
    renderChoices();

    await user.click(await screen.findByRole("button", { name: t("oda.sync.button") }));
    const dialog = await screen.findByRole("dialog", { name: t("oda.sync.confirm.title") });
    expect(await within(dialog).findByText(t("oda.sync.confirm.count", { count: 120 }))).toBeInTheDocument();
    const staples = within(dialog).getByRole("checkbox", { name: t("oda.sync.confirm.staples") });
    const existing = within(dialog).getByRole("checkbox", { name: t("oda.sync.confirm.existing") });
    expect(staples).not.toBeChecked();
    expect(existing).not.toBeChecked();
    expect(calls.filter((c) => c.path.endsWith("/start")).map((c) => c.body)).toEqual([{ confirmed: true, includeStaples: false, includeExisting: false, offset: 0 }]);

    startAnswer = () => json({ authorizeUrl: "https://oda.example/o/authorize/?state=s2", ingredientCount: 140, notCheckedCount: 60 });
    await user.click(staples);
    expect(await within(dialog).findByText(t("oda.sync.confirm.count", { count: 140 }))).toBeInTheDocument();
    expect(within(dialog).getByTestId("oda-sync-left")).toHaveTextContent(t("oda.sync.confirm.left", { count: 60 }));
    expect(calls.filter((c) => c.path.endsWith("/start")).at(-1)?.body).toEqual({ confirmed: true, includeStaples: true, includeExisting: false, offset: 0 });

    await user.click(within(dialog).getByRole("button", { name: t("oda.sync.confirm.submit") }));
    expect(go).toHaveBeenCalledWith("https://oda.example/o/authorize/?state=s2");
    const pending = JSON.parse(window.sessionStorage.getItem("foodplanner.oda.pending")!) as Record<string, unknown>;
    expect(pending).toMatchObject({ kind: "sync", householdId: "h1", includeStaples: true, includeExisting: false, offset: 0 });
    expect(Object.keys(pending).sort()).toEqual(["at", "householdId", "includeExisting", "includeStaples", "kind", "offset"]); // numbers and flags only
  });

  it("is not offered to a Child, and goes away when Oda is not set up", async () => {
    role = "Child";
    const child = renderChoices();
    await screen.findByTestId("oda-choices-adults-only");
    expect(screen.queryByRole("button", { name: t("oda.sync.button") })).toBeNull();
    child.unmount();

    role = "Adult";
    startAnswer = () => problem("oda-not-configured", 404);
    const user = userEvent.setup();
    renderChoices();
    await user.click(await screen.findByRole("button", { name: t("oda.sync.button") }));

    expect(await screen.findByTestId("oda-sync-unavailable")).toHaveTextContent(t("oda.sync.notConfigured"));
    expect(screen.queryByRole("button", { name: t("oda.sync.button") })).toBeNull();
  });

  it("explains an empty run and keeps the confirm button off", async () => {
    startAnswer = () => problem("oda-sync-nothing-to-sync", 409);
    const user = userEvent.setup();
    renderChoices();

    await user.click(await screen.findByRole("button", { name: t("oda.sync.button") }));

    const dialog = await screen.findByRole("dialog");
    expect(await within(dialog).findByText(t("oda.sync.problem.nothing"))).toBeInTheDocument();
    expect(within(dialog).getByRole("button", { name: t("oda.sync.confirm.submit") })).toBeDisabled();
  });
});

function arrive(options: { includeStaples?: boolean; includeExisting?: boolean; offset?: number } = {}, query = `?code=${secret}&state=st-1`) {
  rememberSyncPending({ householdId: "h1", includeStaples: false, includeExisting: false, offset: 0, ...options });
  window.history.replaceState(null, "", `/oda/callback${query}`);
}

function renderCallback(strict = false) {
  const page = (
    <MemoryRouter>
      <SessionProvider>
        <OdaCallback />
      </SessionProvider>
    </MemoryRouter>
  );
  return render(strict ? <StrictMode>{page}</StrictMode> : page);
}

const reviews = () => calls.filter((c) => c.path.endsWith("/oda-sync/review"));
const saves = () => calls.filter((c) => c.method === "PUT");

describe("the Sync with Oda review on the callback page", () => {
  it("calls review once (also under StrictMode), clears the code and the pending record, and shows the sections", async () => {
    arrive();
    renderCallback(true);

    const usual = await screen.findByTestId("oda-sync-usually-bought");
    expect(reviews()).toEqual([{ method: "POST", path: "/api/households/h1/oda-sync/review", body: { state: "st-1", code: secret, error: null } }]);
    expect(window.location.search).toBe("");
    expect(window.sessionStorage.getItem("foodplanner.oda.pending")).toBeNull();
    expect(screen.getByRole("heading", { level: 1, name: t("oda.sync.title") })).toBeInTheDocument();
    expect(screen.getByTestId("oda-sync-summary")).toHaveTextContent(t("oda.sync.summary", { usual: 1, suggestions: 2, notFound: 1 }));
    expect(screen.getByText(t("oda.sync.notKept"))).toBeInTheDocument();
    expect(within(usual).getByRole("checkbox", { name: /Rice/ })).toBeChecked(); // usually bought: pre-ticked
    const suggestions = screen.getByTestId("oda-sync-suggestion");
    for (const box of within(suggestions).getAllByRole("checkbox")) expect(box).not.toBeChecked(); // suggestions: never
    expect(within(suggestions).getByRole("checkbox", { name: /Pasta/ })).toHaveAccessibleName(expect.stringContaining(t("oda.sync.replaces")));
    expect(within(screen.getByTestId("oda-sync-not-found")).getByText(/Coffee filters/)).toHaveTextContent(t("oda.sync.reason.no-match"));
    expect(within(screen.getByTestId("oda-sync-not-found")).queryByRole("checkbox")).toBeNull(); // not found: no control
    expect(screen.queryByTestId("oda-sync-not-closed")).toBeNull();
    expect(screen.getByRole("button", { name: t("oda.sync.save", { count: 1 }) })).toBeEnabled();
  });

  it("ticks a whole section, picks another product (which ticks the row) and saves only the ticked rows", async () => {
    const user = userEvent.setup();
    arrive();
    renderCallback();
    await screen.findByTestId("oda-sync-suggestion");

    await user.click(screen.getByRole("button", { name: t("oda.sync.tickAllFor", { section: t("oda.sync.section.suggestion") }) }));
    expect(screen.getByRole("button", { name: t("oda.sync.save", { count: 3 }) })).toBeEnabled();
    await user.click(screen.getByRole("button", { name: t("oda.sync.untickAllFor", { section: t("oda.sync.section.suggestion") }) }));
    expect(screen.getByRole("button", { name: t("oda.sync.save", { count: 1 }) })).toBeEnabled();

    // Choose another for Egg: the radio list opens, a different option selects it and ticks the row.
    await user.click(screen.getByRole("button", { name: t("oda.sync.chooseAnotherFor", { name: "Egg" }) }));
    await user.click(screen.getByRole("radio", { name: new RegExp("Organic eggs") }));
    expect(screen.getByRole("checkbox", { name: /Egg/ })).toBeChecked();
    // The current choice of Milk can be replaced by picking another option; that ticks it with replace.
    await user.click(screen.getByRole("button", { name: t("oda.sync.chooseAnotherFor", { name: "Milk" }) }));
    await user.click(screen.getByRole("radio", { name: new RegExp("Helmelk") }));
    expect(screen.getByRole("checkbox", { name: /Milk/ })).toBeChecked();

    await user.click(screen.getByRole("button", { name: t("oda.sync.save", { count: 3 }) }));

    await waitFor(() => expect(saves()).toHaveLength(1));
    expect(saves()[0]!.body).toEqual({
      items: [
        { ingredientId: "i-rice", odaProductId: 1004, replace: false },
        { ingredientId: "i-egg", odaProductId: 1007, replace: false },
        { ingredientId: "i-milk", odaProductId: 2002, replace: true },
      ],
    });
    expect(await screen.findByTestId("oda-sync-saved")).toHaveTextContent(t("oda.sync.saved", { count: 3 }));
    expect(screen.getByRole("link", { name: t("oda.choices.link") })).toHaveAttribute("href", "/shopping/oda-choices");
    expect(screen.queryByRole("checkbox", { name: /Rice/ })).toBeNull(); // saved rows leave the list
  });

  it("sends replace: true only for ticked rows that had a choice and reports the ones that were kept", async () => {
    const user = userEvent.setup();
    saveAnswer = () => json({ created: 0, replaced: 0, unchanged: 0, skippedExisting: ["i-pasta"] });
    arrive();
    renderCallback();
    await screen.findByTestId("oda-sync-suggestion");

    await user.click(screen.getByRole("checkbox", { name: /Pasta/ }));
    await user.click(screen.getByRole("button", { name: t("oda.sync.save", { count: 2 }) }));

    await waitFor(() => expect(saves()).toHaveLength(1));
    expect(saves()[0]!.body).toEqual({
      items: [
        { ingredientId: "i-rice", odaProductId: 1004, replace: false },
        { ingredientId: "i-pasta", odaProductId: 3001, replace: true },
      ],
    });
    expect(await screen.findByTestId("oda-sync-saved")).toHaveTextContent(t("oda.sync.kept", { count: 1 }));
    expect(screen.getByRole("checkbox", { name: /Pasta/ })).toBeInTheDocument(); // the kept one stays in the list
  });

  it("keeps the list and the ticks and shows the message when the save fails", async () => {
    const user = userEvent.setup();
    saveAnswer = () => problem("oda-preference-limit", 409);
    arrive();
    renderCallback();
    await screen.findByTestId("oda-sync-usually-bought");

    await user.click(screen.getByRole("button", { name: t("oda.sync.save", { count: 1 }) }));

    expect(await screen.findByTestId("oda-sync-error")).toHaveTextContent(t("oda.pick.problem.limit"));
    expect(screen.getByRole("checkbox", { name: /Rice/ })).toBeChecked();
    expect(screen.getByRole("button", { name: t("oda.sync.save", { count: 1 }) })).toBeEnabled();
  });

  it("shows a partial result with the reason and 'Check the next ones' that starts a new run behind the checked ones", async () => {
    const user = userEvent.setup();
    const go = vi.spyOn(navigation, "go").mockImplementation(() => undefined);
    reviewAnswer = () => json(review({ status: "partial", stopped: "oda-limit", notCheckedCount: 12 }));
    startAnswer = () => json({ authorizeUrl: "https://oda.example/o/authorize/?state=s3", ingredientCount: 12, notCheckedCount: 0 });
    arrive({ includeStaples: true, offset: 200 });
    renderCallback();

    const partial = await screen.findByTestId("oda-sync-partial");
    expect(partial).toHaveTextContent(t("oda.sync.partial.oda-limit"));
    expect(partial).toHaveTextContent(t("oda.sync.notChecked", { count: 12 }));
    await user.click(within(partial).getByRole("button", { name: t("oda.sync.next") }));
    await user.click(within(await screen.findByRole("dialog")).getByRole("button", { name: t("oda.sync.confirm.submit") }));

    await waitFor(() => expect(go).toHaveBeenCalledWith("https://oda.example/o/authorize/?state=s3"));
    // 200 before, 5 rows searched in this run, none saved: the next run continues at 205 with the same options.
    expect(calls.filter((c) => c.path.endsWith("/start")).at(-1)?.body).toEqual({ confirmed: true, includeStaples: true, includeExisting: false, offset: 205 });
    expect(JSON.parse(window.sessionStorage.getItem("foodplanner.oda.pending")!)).toMatchObject({ kind: "sync", includeStaples: true, offset: 205 });
  });

  it("shows the connection alert only when the connection could not be closed, and the history note only when it could not be read", async () => {
    reviewAnswer = () => json(review({ connectionClosed: false, connectionMessage: "x", usuallyBought: "unavailable" }));
    arrive();
    renderCallback();

    expect(await screen.findByTestId("oda-sync-not-closed")).toHaveTextContent(t("oda.result.notClosed"));
    expect(screen.getByTestId("oda-sync-usual-unavailable")).toHaveTextContent(t("oda.sync.usualUnavailable"));
    expect(screen.queryByTestId("oda-sync-partial")).toBeNull();
  });

  it.each([
    ["502 oda-unavailable", () => problem("oda-unavailable", 502, { connectionClosed: false }), "oda.problem.unavailable"],
    ["400 oda-state-invalid", () => problem("oda-state-invalid", 400), "oda.problem.stateInvalid"],
    ["429 rate-limited", () => problem("rate-limited", 429), "problem.rateLimited"],
  ] as const)("shows a plain message and 'Try again' for %s", async (_name, answer, key) => {
    reviewAnswer = answer;
    arrive();
    renderCallback();

    const failed = await screen.findByTestId("oda-sync-failed");
    expect(failed).toHaveTextContent(t(key));
    expect(within(failed).getByRole("link", { name: t("oda.sync.tryAgain") })).toHaveAttribute("href", "/shopping/oda-choices");
    expect(failed).not.toHaveTextContent(/oda\.com|upstream|token/i);
  });

  it("shows a network failure as a plain message and does not call review twice", async () => {
    reviewAnswer = () => {
      throw new TypeError("network");
    };
    arrive();
    renderCallback(true);

    expect(await screen.findByTestId("oda-sync-failed")).toHaveTextContent(t("problem.generic"));
    expect(reviews()).toHaveLength(1);
  });

  it("handles a cancelled sign-in without a code and a reload without a pending record", async () => {
    reviewAnswer = () => json(review({ status: "cancelled", rows: [] }));
    arrive({}, "?error=access_denied&state=st-1");
    const first = renderCallback();
    expect(await screen.findByTestId("oda-sync-cancelled")).toHaveTextContent(t("oda.sync.cancelled"));
    expect(reviews()[0]!.body).toEqual({ state: "st-1", code: null, error: "access_denied" });
    first.unmount();

    resetOdaCallbackForTests();
    renderCallback();
    expect(await screen.findByTestId("oda-nothing")).toBeInTheDocument();
    expect(reviews()).toHaveLength(1);
  });

  it("writes nothing from Oda to browser storage", async () => {
    const user = userEvent.setup();
    arrive();
    renderCallback();
    await screen.findByTestId("oda-sync-usually-bought");
    await user.click(screen.getByRole("button", { name: t("oda.sync.save", { count: 1 }) }));
    await screen.findByTestId("oda-sync-saved");

    const stored = JSON.stringify({ ...window.localStorage, ...window.sessionStorage });
    for (const text of [secret, "st-1", "Basmati", "Jasmine", "Rice", "1004", "oda.example"]) expect(stored).not.toContain(text);
  });
});
