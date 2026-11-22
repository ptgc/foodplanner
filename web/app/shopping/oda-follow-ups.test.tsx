import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { beforeEach, describe, expect, it } from "vitest";
import type { components } from "../api/schema";
import { pendingTtlMs, rememberPending, takePending } from "./oda";
import { addedLine } from "./oda-fixtures";
import { OdaResult } from "./oda-result";

type Cart = components["schemas"]["OdaCartResponse"];

const pending = { householdId: "h1", weekStart: "2026-10-19" };

function cartWith(quantity: unknown): Cart {
  return {
    status: "done",
    cartUrl: "https://oda.example/cart",
    added: [addedLine({ itemId: "i1", name: "Melk", productName: "Tine Lettmelk 1 l", quantity: quantity as number, source: "first-hit" })],
    notAdded: [],
    cartMessages: [],
    connectionClosed: true,
    connectionMessage: null,
    usuallyBought: { status: "used", count: 0 },
  };
}

function show(quantity: unknown) {
  const { unmount } = render(
      <MemoryRouter>
        <OdaResult result={{ kind: "result", cart: cartWith(quantity) }} pending={pending} />
      </MemoryRouter>
  );
  const text = screen.getByTestId("oda-added").textContent ?? "";
  unmount();
  return text;
}

describe("Oda result quantities (T-116)", () => {
  it.each([undefined, null, "", "NaN", Number.NaN, {}])("shows no quantity and no NaN for %p", (q) => {
    const text = show(q);
    expect(text).not.toMatch(/NaN/i);
    expect(text).toContain("Melk");
    expect(text).toContain("Tine Lettmelk 1 l");
    expect(text).not.toMatch(/,\s*$/);
  });

  it("shows the raw text when the quantity is not a number", () => {
    expect(show("2 pk")).toContain("2 pk");
  });

  it("formats a numeric quantity", () => {
    expect(show(3)).toMatch(/3/);
    expect(show("2")).toMatch(/2/);
  });
});

describe("Oda pending entry (T-116)", () => {
  beforeEach(() => window.sessionStorage.clear());

  it("is returned within 10 minutes and cleared", () => {
    rememberPending(pending, 1_000);
    expect(takePending(1_000 + pendingTtlMs)).toEqual(pending);
    expect(window.sessionStorage.length).toBe(0);
  });

  it("is ignored and removed when older than 10 minutes", () => {
    rememberPending(pending, 1_000);
    expect(takePending(1_000 + pendingTtlMs + 1)).toBeNull();
    expect(window.sessionStorage.length).toBe(0);
  });

  it("ignores an old entry without a timestamp", () => {
    window.sessionStorage.setItem("foodplanner.oda.pending", JSON.stringify(pending));
    expect(takePending()).toBeNull();
    expect(window.sessionStorage.length).toBe(0);
  });

  it("is overwritten by a new send", () => {
    rememberPending(pending, 1_000);
    rememberPending({ householdId: "h2", weekStart: "2026-10-26" }, 5_000_000);
    expect(takePending(5_000_000 + 1)).toEqual({ householdId: "h2", weekStart: "2026-10-26" });
  });
});
