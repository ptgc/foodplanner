import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { StrictMode } from "react";
import { MemoryRouter } from "react-router";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { resetAntiforgeryToken } from "../api/client";
import type { components } from "../api/schema";
import { rememberPending } from "../shopping/oda";
import { t } from "../test-i18n";
import OdaCallback, { resetOdaCallbackForTests } from "./oda-callback";

type Cart = components["schemas"]["OdaCartResponse"];

const week = "2026-10-19";
const secret = "SECRET-CODE-123";

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": status >= 400 ? "application/problem+json" : "application/json" },
  });

const problem = (type: string, status: number, extra: Record<string, unknown> = {}) => json({ type: `urn:foodplanner:problem:${type}`, title: "x", status, ...extra }, status);

const cart = (over: Partial<Cart> = {}): Cart => ({
  status: "done",
  cartUrl: "https://oda.example/cart",
  added: [{ itemId: "i1", name: "Melk", productName: "Tine Lettmelk 1 l", quantity: 1 }],
  notAdded: [],
  cartMessages: [],
  connectionClosed: true,
  connectionMessage: null,
  ...over,
});

let carts: { path: string; body: unknown }[];
let answer: () => Response;

function install() {
  vi.stubGlobal(
    "fetch",
    vi.fn(async (request: Request) => {
      const path = new URL(request.url).pathname;
      if (path === "/api/auth/antiforgery") return json({ token: "tok" });
      if (path.endsWith("/oda/cart") && request.method === "POST") {
        carts.push({ path, body: await request.json() });
        return answer();
      }
      if (path.endsWith("/export")) return json({ format: "text", title: "Shopping list", text: "- Melk", url: null });
      throw new Error(`unexpected ${request.method} ${path}`);
    }),
  );
}

function arrive(query: string) {
  rememberPending({ householdId: "h1", weekStart: week });
  window.history.replaceState(null, "", `/oda/callback${query}`);
}

function renderPage(strict = false) {
  const page = (
    <MemoryRouter>
      <OdaCallback />
    </MemoryRouter>
  );
  return render(strict ? <StrictMode>{page}</StrictMode> : page);
}

beforeEach(() => {
  resetAntiforgeryToken();
  resetOdaCallbackForTests();
  window.sessionStorage.clear();
  window.localStorage.clear();
  carts = [];
  answer = () => json(cart());
  install();
});
afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  window.history.replaceState(null, "", "/");
});

describe("Oda callback page", () => {
  it("calls oda/cart exactly once (also under StrictMode) and clears code and state from the address bar", async () => {
    arrive(`?code=${secret}&state=st-1`);
    renderPage(true);

    await screen.findByTestId("oda-result");
    expect(carts).toEqual([{ path: `/api/households/h1/shopping-lists/${week}/oda/cart`, body: { state: "st-1", code: secret, error: null } }]);
    expect(window.location.search).toBe("");
    expect(window.location.href).not.toContain(secret);
  });

  it("does not call oda/cart again after a reload or going back (no code in the address bar, week already taken)", async () => {
    arrive(`?code=${secret}&state=st-1`);
    const first = renderPage();
    await screen.findByTestId("oda-result");
    first.unmount();

    // A reload: a fresh page load has no module state either, so the guard that matters is the empty address bar.
    resetOdaCallbackForTests();
    renderPage();
    expect(await screen.findByTestId("oda-nothing")).toHaveTextContent(t("oda.callback.nothingPending"));
    expect(carts).toHaveLength(1);
  });

  it("writes nothing from Oda to browser storage", async () => {
    answer = () => json(cart({ cartMessages: ["Sold out"], added: [{ itemId: "i1", name: "Melk", productName: "Tine Lettmelk", quantity: 1 }] }));
    arrive(`?code=${secret}&state=st-1`);
    renderPage();
    await screen.findByTestId("oda-result");

    const stored = JSON.stringify({ ...window.localStorage, ...window.sessionStorage });
    for (const text of [secret, "st-1", "Tine Lettmelk", "Sold out", "oda.example"]) expect(stored).not.toContain(text);
    expect(window.sessionStorage.getItem("foodplanner.oda.pending")).toBeNull(); // read once, then cleared
  });

  it("shows the result: added, not added with reasons, cart messages, the cart link", async () => {
    answer = () =>
      json(
        cart({
          status: "partial",
          added: [{ itemId: "i1", name: "Melk", productName: "Tine Lettmelk 1 l", quantity: 1 }],
          notAdded: [
            { itemId: "i2", name: "Kaffe", reason: "no-match" },
            { itemId: "i3", name: "Egg", reason: "unavailable" },
            { itemId: "i4", name: "Ost", reason: "rejected" },
          ],
          cartMessages: ["Egg is sold out"],
        }),
      );
    arrive(`?code=${secret}&state=st-1`);
    renderPage();
    const result = await screen.findByTestId("oda-result");

    expect(within(result).getByText(t("oda.result.partial"))).toBeInTheDocument();
    expect(within(screen.getByTestId("oda-added")).getByText(t("oda.result.addedLine", { name: "Melk", productName: "Tine Lettmelk 1 l", quantity: "1" }))).toBeInTheDocument();
    const notAdded = within(screen.getByTestId("oda-not-added"));
    expect(notAdded.getByText(t("oda.result.notAddedLine", { name: "Kaffe", reason: t("oda.reason.no-match") }))).toBeInTheDocument();
    expect(notAdded.getByText(t("oda.result.notAddedLine", { name: "Egg", reason: t("oda.reason.unavailable") }))).toBeInTheDocument();
    expect(notAdded.getByText(t("oda.result.notAddedLine", { name: "Ost", reason: t("oda.reason.rejected") }))).toBeInTheDocument();
    expect(within(screen.getByTestId("oda-messages")).getByText("Egg is sold out")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: t("oda.result.openCart") })).toHaveAttribute("href", "https://oda.example/cart");
    expect(screen.queryByTestId("oda-not-closed")).toBeNull();
  });

  it("renders Oda text as plain text, never as HTML, and ignores a non-http cart link", async () => {
    answer = () =>
      json(
        cart({
          cartUrl: "javascript:alert(1)",
          cartMessages: ['<img src=x onerror="alert(1)"> <b>bold</b>'],
          added: [{ itemId: "i1", name: "Melk", productName: "<script>alert(1)</script>", quantity: 1 }],
        }),
      );
    arrive(`?code=${secret}&state=st-1`);
    renderPage();
    const result = await screen.findByTestId("oda-result");

    expect(result.querySelector("img, script, b")).toBeNull();
    expect(within(screen.getByTestId("oda-messages")).getByText('<img src=x onerror="alert(1)"> <b>bold</b>')).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: t("oda.result.openCart") })).toBeNull();
  });

  it("tells the user to revoke in Oda when the connection was not closed", async () => {
    answer = () => json(cart({ connectionClosed: false, connectionMessage: "x" }));
    arrive(`?code=${secret}&state=st-1`);
    renderPage();
    expect(await screen.findByTestId("oda-not-closed")).toHaveTextContent(t("oda.result.notClosed"));
  });

  it("passes the denial on once and shows that nothing was added", async () => {
    answer = () => json(cart({ status: "cancelled", cartUrl: null, added: [] }));
    arrive("?error=access_denied&state=st-1");
    renderPage();
    const result = await screen.findByTestId("oda-result");

    expect(carts.map((c) => c.body)).toEqual([{ state: "st-1", code: null, error: "access_denied" }]);
    expect(within(result).getByText(t("oda.result.cancelled"))).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: t("oda.result.openCart") })).toBeNull();
  });

  it("shows a friendly message for an expired sign-in (400 oda-state-invalid) and offers no text export", async () => {
    answer = () => problem("oda-state-invalid", 400);
    arrive(`?code=${secret}&state=st-1`);
    renderPage();
    const failed = await screen.findByTestId("oda-failed");

    expect(within(failed).getByText(t("oda.problem.stateInvalid"))).toBeInTheDocument();
    expect(screen.queryByTestId("oda-text-fallback")).toBeNull();
    expect(carts).toHaveLength(1);
  });

  it("on 502 shows what was already added, the not-closed note, and the text export", async () => {
    answer = () =>
      problem("oda-unavailable", 502, {
        added: [{ itemId: "i1", name: "Melk", productName: "Tine Lettmelk 1 l", quantity: 1 }],
        connectionClosed: false,
        fallback: { export: "text" },
      });
    arrive(`?code=${secret}&state=st-1`);
    renderPage();
    const failed = await screen.findByTestId("oda-failed");

    expect(within(failed).getByText(t("oda.problem.unavailable"))).toBeInTheDocument();
    expect(within(screen.getByTestId("oda-added")).getByText(t("oda.result.addedLine", { name: "Melk", productName: "Tine Lettmelk 1 l", quantity: "1" }))).toBeInTheDocument();
    expect(screen.getByTestId("oda-not-closed")).toBeInTheDocument();
    const fallback = screen.getByTestId("oda-text-fallback");
    expect(within(fallback).getByRole("button", { name: t("shopping.copy") })).toBeEnabled();
    expect(within(fallback).getByRole("button", { name: t("shopping.share") })).toBeEnabled();
    expect(carts).toHaveLength(1); // never retried

    // The export works from here: it asks the API for the text of the same week.
    await userEvent.click(within(fallback).getByRole("button", { name: t("shopping.copy") }));
    await waitFor(() => expect(vi.mocked(fetch).mock.calls.some(([r]) => (r as Request).url.includes(`/shopping-lists/${week}/export`))).toBe(true));
  });

  it("offers the text export after 429 and a network failure, and never retries", async () => {
    answer = () => problem("rate-limited", 429);
    arrive(`?code=${secret}&state=st-1`);
    renderPage();
    const failed = await screen.findByTestId("oda-failed");
    expect(within(failed).getByText(t("problem.rateLimited"))).toBeInTheDocument();
    expect(screen.getByTestId("oda-text-fallback")).toBeInTheDocument();
    expect(carts).toHaveLength(1);
  });

  it("says there is nothing to finish when the address has no code, and calls nothing", async () => {
    rememberPending({ householdId: "h1", weekStart: week });
    window.history.replaceState(null, "", "/oda/callback");
    renderPage();
    expect(await screen.findByTestId("oda-nothing")).toBeInTheDocument();
    expect(carts).toHaveLength(0);
  });
});
