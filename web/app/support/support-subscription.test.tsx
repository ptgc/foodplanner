import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { resetAntiforgeryToken } from "../api/client";
import type { components } from "../api/schema";
import { SessionProvider } from "../session";
import { t } from "../test-i18n";
import { supportEn } from "../i18n/support-en";
import { supportNb } from "../i18n/support-nb";
import { SupportPanels, type PanelId } from "./support-panels";

type Plan = components["schemas"]["SupportPlanResponse"];

const household = "22222222-2222-2222-2222-222222222222";
const base = `/api/support/households/${household}`;

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": status >= 400 ? "application/problem+json" : "application/json" },
  });

const defaults: Plan["defaults"] = {
  photoImportsPerDay: { unlimited: false, limit: 2 },
  lineHelpPerDay: { unlimited: false, limit: 20 },
  lineHelpPerMonth: { unlimited: false, limit: 200 },
};
const defaultPlan: Plan = { source: "Default", name: null, ...defaults, defaults };
const customPlan: Plan = {
  source: "Custom",
  name: "Family",
  photoImportsPerDay: { unlimited: true, limit: null },
  lineHelpPerDay: { unlimited: false, limit: 40 },
  lineHelpPerMonth: { unlimited: false, limit: 300 },
  defaults,
};

type Call = { method: string; path: string; body: unknown };
let plan: Plan;
let calls: Call[];
let putStatus: number;

beforeEach(() => {
  resetAntiforgeryToken();
  calls = [];
  putStatus = 200;
  plan = defaultPlan;
  vi.stubGlobal(
    "fetch",
    vi.fn(async (request: Request) => {
      const url = new URL(request.url);
      const method = request.method;
      if (url.pathname === "/api/auth/me") return json({ userId: "33333333-3333-3333-3333-333333333333", email: "admin@example.test", kidMode: null, isSystemAdmin: true });
      if (url.pathname === "/api/auth/antiforgery") return json({ token: "t" });
      if (url.pathname === "/api/households") return json([]);
      const body = method === "GET" || method === "DELETE" ? null : await request.json().catch(() => null);
      calls.push({ method, path: url.pathname, body });
      if (url.pathname === `${base}/plan`) {
        if (method === "GET") return json(plan);
        if (method === "PUT") {
          if (putStatus === 400) return json({ type: "urn:foodplanner:problem:validation", title: "x", status: 400, errors: { lineHelpPerDay: ["bad"] } }, 400);
          plan = { ...customPlan, name: (body as { name: string | null }).name };
          return json(plan);
        }

        plan = defaultPlan;
        return new Response(null, { status: 204 });
      }

      if (url.pathname === base) {
        return json({ id: household, name: "Hansen", createdAt: "2026-01-02T00:00:00Z", plan: plan.name, memberCount: 1, ownerCount: 1, openInviteCount: 0, profileCount: 0, recipeCount: 0, mealPlanCount: 0 });
      }

      throw new Error(`unexpected ${method} ${url.pathname}`);
    }),
  );
});
afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

function renderPanel(panel: PanelId, write: boolean) {
  return render(
    <MemoryRouter>
      <SessionProvider>
        <SupportPanels panel={panel} householdId={household} householdName="Hansen" write={write} />
      </SessionProvider>
    </MemoryRouter>,
  );
}

describe("Subscription_panel_read_session", () => {
  it("shows source, label and the three limits with the default beside a custom one, and no form or buttons", async () => {
    plan = customPlan;
    renderPanel("subscription", false);

    expect(await screen.findByTestId("support-subscription-source")).toHaveTextContent(t("support.subscription.source.Custom"));
    expect(screen.getByText("Family")).toBeInTheDocument();
    expect(screen.getByTestId("support-subscription-photoImportsPerDay")).toHaveTextContent(`${t("support.subscription.unlimited")} (${t("support.subscription.defaultIs", { value: "2" })})`);
    expect(screen.getByTestId("support-subscription-lineHelpPerDay")).toHaveTextContent(`40 (${t("support.subscription.defaultIs", { value: "20" })})`);
    expect(screen.getByTestId("support-subscription-lineHelpPerMonth")).toHaveTextContent(`300 (${t("support.subscription.defaultIs", { value: "200" })})`);
    expect(screen.queryByRole("form")).not.toBeInTheDocument();
    expect(screen.queryByRole("button")).not.toBeInTheDocument();
    expect(screen.queryByRole("checkbox")).not.toBeInTheDocument();
  });

  it("shows a default plan without 'default:' notes", async () => {
    renderPanel("subscription", false);

    expect(await screen.findByTestId("support-subscription-source")).toHaveTextContent(t("support.subscription.source.Default"));
    expect(screen.getByTestId("support-subscription-photoImportsPerDay")).toHaveTextContent(/^2$/);
    expect(screen.getByTestId("support-subscription-photoImportsPerDay")).not.toHaveTextContent("default");
  });

  it("shows the plan label, or 'Default', in the overview's plan row", async () => {
    plan = customPlan;
    const { unmount } = renderPanel("overview", false);
    expect(await screen.findByTestId("support-plan")).toHaveTextContent("Family");
    unmount();

    plan = defaultPlan;
    renderPanel("overview", false);
    expect(await screen.findByTestId("support-plan")).toHaveTextContent(t("support.overview.noPlan"));
    expect(t("support.overview.noPlan")).toMatch(/^(Default|Standard)$/);
  });
});

describe("Subscription_panel_write_session", () => {
  it("disables the number field while Unlimited is on and hides Reset for a default plan", async () => {
    const user = userEvent.setup();
    renderPanel("subscription", true);
    const photo = t("support.subscription.photoImportsPerDay");
    const number = await screen.findByLabelText(t("support.subscription.numberFor", { field: photo }));

    expect(number).toBeEnabled();
    expect(number).toHaveValue(2);
    await user.click(screen.getByRole("checkbox", { name: t("support.subscription.unlimitedFor", { field: photo }) }));
    expect(number).toBeDisabled();
    expect(screen.queryByRole("button", { name: t("support.subscription.reset") })).not.toBeInTheDocument(); // source Default
    expect(screen.getByRole("button", { name: t("support.subscription.save") })).toHaveClass("button"); // .button carries the 44px minimum
  });

  it("opens a confirmation naming the household and the effect, and sends the call only after confirming", async () => {
    const user = userEvent.setup();
    renderPanel("subscription", true);
    const photo = t("support.subscription.photoImportsPerDay");
    await screen.findByLabelText(t("support.subscription.labelField"));
    await user.type(screen.getByLabelText(t("support.subscription.labelField")), "Family");
    await user.click(screen.getByRole("checkbox", { name: t("support.subscription.unlimitedFor", { field: photo }) }));

    await user.click(screen.getByRole("button", { name: t("support.subscription.save") }));

    const dialog = await screen.findByRole("dialog");
    const effect = t("support.subscription.effect", { photo: t("support.subscription.unlimited"), day: "20", month: "200" });
    expect(dialog).toHaveTextContent(t("support.subscription.confirmSet", { household: "Hansen", effect }));
    expect(calls.some((c) => c.method === "PUT")).toBe(false);

    await user.click(within(dialog).getByRole("button", { name: t("support.confirm.yes") }));

    await waitFor(() => expect(calls.find((c) => c.method === "PUT")).toBeDefined());
    expect(calls.find((c) => c.method === "PUT")).toMatchObject({
      path: `${base}/plan`,
      body: {
        name: "Family",
        photoImportsPerDay: { unlimited: true, limit: null },
        lineHelpPerDay: { unlimited: false, limit: 20 },
        lineHelpPerMonth: { unlimited: false, limit: 200 },
      },
    });
    expect(await screen.findByTestId("support-subscription-source")).toHaveTextContent(t("support.subscription.source.Custom"));
  });

  it("cancelling the confirmation sends nothing", async () => {
    const user = userEvent.setup();
    renderPanel("subscription", true);
    await user.click(await screen.findByRole("button", { name: t("support.subscription.save") }));
    const dialog = await screen.findByRole("dialog");

    await user.click(within(dialog).getByRole("button", { name: t("household.dialog.cancel") }));

    expect(calls.some((c) => c.method === "PUT")).toBe(false);
  });

  it("resets a custom plan after a confirmation naming the household and the defaults", async () => {
    plan = customPlan;
    const user = userEvent.setup();
    renderPanel("subscription", true);

    await user.click(await screen.findByRole("button", { name: t("support.subscription.reset") }));

    const dialog = await screen.findByRole("dialog");
    const effect = t("support.subscription.effect", { photo: "2", day: "20", month: "200" });
    expect(dialog).toHaveTextContent(t("support.subscription.confirmReset", { household: "Hansen", effect }));
    expect(calls.some((c) => c.method === "DELETE")).toBe(false);
    await user.click(within(dialog).getByRole("button", { name: t("support.confirm.yes") }));

    await waitFor(() => expect(calls.some((c) => c.method === "DELETE" && c.path === `${base}/plan`)).toBe(true));
    expect(await screen.findByTestId("support-subscription-source")).toHaveTextContent(t("support.subscription.source.Default"));
  });

  it("shows a 400 beside its field and closes the confirmation", async () => {
    putStatus = 400;
    const user = userEvent.setup();
    renderPanel("subscription", true);
    const day = t("support.subscription.lineHelpPerDay");
    const number = await screen.findByLabelText(t("support.subscription.numberFor", { field: day }));
    await user.clear(number);
    await user.click(screen.getByRole("button", { name: t("support.subscription.save") }));

    await user.click(within(await screen.findByRole("dialog")).getByRole("button", { name: t("support.confirm.yes") }));

    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent(t("support.subscription.error.limit"));
    expect(number).toHaveAttribute("aria-invalid", "true");
    expect(number.closest(".field")).toContainElement(alert); // beside the field
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });

  it("has every control as a 44px-class control", async () => {
    plan = customPlan;
    renderPanel("subscription", true);
    await screen.findByRole("button", { name: t("support.subscription.reset") });

    for (const button of screen.getAllByRole("button")) expect(button).toHaveClass("button");
    for (const box of screen.getAllByRole("checkbox")) expect(box.closest("label")).toHaveClass("support-radio"); // min-height: var(--touch)
    for (const input of [...screen.getAllByRole("spinbutton"), screen.getByLabelText(t("support.subscription.labelField"))]) {
      expect(input.closest(".field")).not.toBeNull(); // .field input: min-height var(--touch)
    }
  });

  it("has every string in nb and en", () => {
    const keys = Object.keys(supportEn).filter((k) => k.startsWith("support.subscription.") || k === "support.tab.subscription");
    expect(keys.length).toBeGreaterThan(15);
    for (const key of keys) expect(supportNb, key).toHaveProperty([key]);
  });
});
