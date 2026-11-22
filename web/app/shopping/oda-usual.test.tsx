import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { afterEach, describe, expect, it } from "vitest";
import type { components } from "../api/schema";
import { translate, type Locale } from "../i18n";
import { t } from "../test-i18n";
import { addedLine } from "./oda-fixtures";
import { OdaResult } from "./oda-result";

type Cart = components["schemas"]["OdaCartResponse"];

const pending = { householdId: "h1", weekStart: "2026-10-19" };

function cart(over: Partial<Cart> = {}): Cart {
  return {
    status: "done",
    cartUrl: "https://oda.example/cart",
    added: [
      addedLine({ itemId: "i1", name: "Tomat", productName: "Cherrytomater 250 g", quantity: 1, source: "usually-bought" }),
      addedLine({ itemId: "i2", name: "Melk", productName: "Tine Lettmelk 1 l", quantity: 1, source: "first-hit" }),
    ],
    notAdded: [],
    cartMessages: [],
    connectionClosed: true,
    connectionMessage: null,
    usuallyBought: { status: "used", count: 1 },
    ...over,
  };
}

function show(result: Cart) {
  return render(
    <MemoryRouter>
      <OdaResult result={{ kind: "result", cart: result }} pending={pending} />
    </MemoryRouter>,
  );
}

afterEach(() => {
  window.localStorage.clear();
  window.sessionStorage.clear();
});

describe("Oda result marks usually bought lines (T-114)", () => {
  it("shows the label on a usually bought line only, in the active language", () => {
    show(cart());

    const lines = screen.getAllByRole("listitem").filter((li) => li.getAttribute("data-source"));
    expect(lines).toHaveLength(2);
    expect(lines[0]).toHaveTextContent(t("oda.result.usual"));
    expect(lines[1]).not.toHaveTextContent(t("oda.result.usual"));
    expect(screen.getAllByTestId("oda-usual")).toHaveLength(1);
    expect(screen.queryByTestId("oda-usual-unavailable")).toBeNull();
  });

  it("has the label in Norwegian and in English", () => {
    const locales: Locale[] = ["nb", "en"];
    expect(locales.map((l) => translate(l, "oda.result.usual"))).toEqual(["Du kjøper ofte denne", "Usually bought"]);
  });

  it("says once, in one plain line, that the usual products could not be read", () => {
    show(cart({ usuallyBought: { status: "unavailable", count: 0 }, added: cart().added.map((a) => ({ ...a, source: "first-hit" })) }));

    expect(screen.getByTestId("oda-usual-unavailable")).toHaveTextContent(t("oda.result.usualUnavailable"));
    expect(screen.queryByTestId("oda-usual")).toBeNull();
  });

  it("shows no note when the history was used or skipped, and no 'connection closed' line", () => {
    for (const status of ["used", "skipped"] as const) {
      const { unmount } = show(cart({ usuallyBought: { status, count: 0 } }));
      expect(screen.queryByTestId("oda-usual-unavailable")).toBeNull();
      expect(screen.queryByTestId("oda-not-closed")).toBeNull();
      unmount();
    }
  });

  it("keeps the alert when the connection could not be closed", () => {
    show(cart({ connectionClosed: false }));

    expect(screen.getByTestId("oda-not-closed")).toBeInTheDocument();
  });

  it("renders product names as plain text and writes nothing to browser storage", () => {
    show(cart({ added: [addedLine({ itemId: "i1", name: "Tomat", productName: "<img src=x onerror=alert(1)>", quantity: 1, source: "usually-bought" })] }));

    expect(document.querySelector("img")).toBeNull();
    expect(window.localStorage.length).toBe(0);
    expect(window.sessionStorage.length).toBe(0);
  });
});
