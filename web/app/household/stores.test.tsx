import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { resetAntiforgeryToken } from "../api/client";
import type { components } from "../api/schema";
import { STORE_SECTIONS } from "../shopping/store-sections";
import { t } from "../test-i18n";
import { moveSection, Stores } from "./stores";

type Store = components["schemas"]["StoreResponse"];

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": status >= 400 ? "application/problem+json" : "application/json" },
  });

const rema: Store = { id: "s1", name: "Rema Lade", sectionOrder: ["Dairy", "FruitAndVegetables", "MeatFishAndCheese", "Frozen", "DryGoods", "Other"] };

type Call = { method: string; path: string; body: unknown };
let stores: Store[];
let calls: Call[];
let failure: Response | null;

beforeEach(() => {
  resetAntiforgeryToken();
  stores = [rema];
  calls = [];
  failure = null;
  vi.stubGlobal(
    "fetch",
    vi.fn(async (request: Request) => {
      const path = new URL(request.url).pathname;
      const method = request.method;
      if (path === "/api/auth/antiforgery") return json({ token: "t" });
      const body = method === "GET" || method === "DELETE" ? null : await request.json().catch(() => null);
      if (path.includes("/stores")) {
        if (method !== "GET") calls.push({ method, path, body });
        if (method !== "GET" && failure) return failure;
        const id = path.split("/stores/")[1];
        if (method === "GET") return json(stores);
        if (method === "POST") {
          const b = body as { name: string; sectionOrder: Store["sectionOrder"] };
          stores = [...stores, { id: "s2", name: b.name, sectionOrder: b.sectionOrder }];
          return json(stores[stores.length - 1], 201);
        }
        if (method === "PUT") {
          const b = body as { name: string; sectionOrder: Store["sectionOrder"] };
          stores = stores.map((s) => (s.id === id ? { ...s, ...b } : s));
          return json(stores.find((s) => s.id === id));
        }
        stores = stores.filter((s) => s.id !== id);
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

describe("moveSection", () => {
  it("swaps a section with its neighbour and leaves the ends alone", () => {
    expect(moveSection(["Dairy", "Frozen", "Other"], 1, -1)).toEqual(["Frozen", "Dairy", "Other"]);
    expect(moveSection(["Dairy", "Frozen", "Other"], 1, 1)).toEqual(["Dairy", "Other", "Frozen"]);
    const same = ["Dairy", "Frozen"] as const;
    expect(moveSection([...same], 0, -1)).toEqual(same);
    expect(moveSection([...same], 1, 1)).toEqual(same);
  });
});

describe("stores on the household page", () => {
  it("lists the stores with their section order; an adult sees edit, delete and the add form", async () => {
    render(<Stores householdId="h1" canEdit />);

    const row = await screen.findByTestId("store-row");
    expect(row).toHaveTextContent("Rema Lade");
    expect(row).toHaveTextContent(t("storeSection.Dairy"));
    expect(screen.getByRole("button", { name: t("household.stores.editFor", { name: "Rema Lade" }) })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: t("household.stores.addTitle") })).toBeInTheDocument();
  });

  it("a child sees the stores but no controls to change them", async () => {
    render(<Stores householdId="h1" canEdit={false} />);

    await screen.findByTestId("store-row");
    expect(screen.queryByRole("button", { name: t("household.stores.editFor", { name: "Rema Lade" }) })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: t("household.stores.add") })).not.toBeInTheDocument();
    expect(screen.getByText(t("household.stores.adultsOnly"))).toBeInTheDocument();
  });

  it("shows a hint when there are no stores", async () => {
    stores = [];
    render(<Stores householdId="h1" canEdit />);

    expect(await screen.findByTestId("stores-empty")).toHaveTextContent(t("household.stores.empty"));
  });

  it("creates a store with the sections moved with the buttons; every section is sent once", async () => {
    stores = [];
    render(<Stores householdId="h1" canEdit />);
    await screen.findByTestId("stores-empty");

    await userEvent.type(screen.getByLabelText(t("household.stores.name")), "Kiwi sentrum");
    await userEvent.click(screen.getByRole("button", { name: t("household.stores.moveUp", { name: t("storeSection.Dairy") }) })); // Dairy was 4th
    await userEvent.click(screen.getByRole("button", { name: t("household.stores.moveUp", { name: t("storeSection.Dairy") }) })); // now 2nd
    await userEvent.click(screen.getByRole("button", { name: t("household.stores.moveUp", { name: t("storeSection.Dairy") }) })); // now first
    await userEvent.click(screen.getByRole("button", { name: t("household.stores.add") }));

    await waitFor(() => expect(calls).toHaveLength(1));
    const sent = calls[0]!.body as { name: string; sectionOrder: string[] };
    expect(calls[0]!.method).toBe("POST");
    expect(sent.name).toBe("Kiwi sentrum");
    expect(sent.sectionOrder).toEqual(["Dairy", "FruitAndVegetables", "MeatFishAndCheese", "Frozen", "DryGoods", "Other"]);
    expect([...sent.sectionOrder].sort()).toEqual([...STORE_SECTIONS].sort());
    expect(await screen.findAllByTestId("store-row")).toHaveLength(1);
  });

  it("the first section cannot move up and the last cannot move down; the buttons are at least 44 px by css", async () => {
    render(<Stores householdId="h1" canEdit />);
    await screen.findByTestId("store-row");

    const rows = screen.getAllByTestId("store-order-row");
    expect(within(rows[0]!).getAllByRole("button")[0]).toBeDisabled();
    expect(within(rows[rows.length - 1]!).getAllByRole("button")[1]).toBeDisabled();
  });

  it("edits a store: renames it and reorders", async () => {
    render(<Stores householdId="h1" canEdit />);
    await screen.findByTestId("store-row");

    await userEvent.click(screen.getByRole("button", { name: t("household.stores.editFor", { name: "Rema Lade" }) }));
    const form = screen.getByTestId("store-edit");
    const name = within(form).getByLabelText(t("household.stores.name"));
    await userEvent.clear(name);
    await userEvent.type(name, "Rema 1000");
    await userEvent.click(within(form).getByRole("button", { name: t("household.stores.moveDown", { name: t("storeSection.Dairy") }) }));
    await userEvent.click(within(form).getByRole("button", { name: t("household.stores.save") }));

    await waitFor(() => expect(calls).toHaveLength(1));
    expect(calls[0]).toEqual({
      method: "PUT",
      path: "/api/households/h1/stores/s1",
      body: { name: "Rema 1000", sectionOrder: ["FruitAndVegetables", "Dairy", "MeatFishAndCheese", "Frozen", "DryGoods", "Other"] },
    });
  });

  it("deletes a store after confirming", async () => {
    render(<Stores householdId="h1" canEdit />);
    await screen.findByTestId("store-row");

    await userEvent.click(screen.getByRole("button", { name: t("household.stores.deleteFor", { name: "Rema Lade" }) }));
    const dialog = await screen.findByRole("dialog");
    expect(calls).toHaveLength(0);
    await userEvent.click(within(dialog).getByRole("button", { name: t("household.stores.delete") }));

    await waitFor(() => expect(calls).toEqual([{ method: "DELETE", path: "/api/households/h1/stores/s1", body: null }]));
    expect(await screen.findByTestId("stores-empty")).toBeInTheDocument();
  });

  it("shows the translated message when the name is taken", async () => {
    failure = json({ status: 409, title: "x", type: "urn:foodplanner:problem:store-exists" }, 409);
    render(<Stores householdId="h1" canEdit />);
    await screen.findByTestId("store-row");

    await userEvent.type(screen.getByLabelText(t("household.stores.name")), "Rema Lade");
    await userEvent.click(screen.getByRole("button", { name: t("household.stores.add") }));

    expect(await screen.findByRole("alert")).toHaveTextContent(t("household.stores.problem.exists"));
  });
});
