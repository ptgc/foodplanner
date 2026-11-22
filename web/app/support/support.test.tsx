import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { resetAntiforgeryToken } from "../api/client";
import type { components } from "../api/schema";
import { SessionProvider } from "../session";
import { t } from "../test-i18n";
import SupportPage from "./support-page";

type SupportSession = components["schemas"]["SupportSessionResponse"];

const household = "22222222-2222-2222-2222-222222222222";
const owner = "33333333-3333-3333-3333-333333333333";
const adult = "44444444-4444-4444-4444-444444444444";
const inviteId = "55555555-5555-5555-5555-555555555555";

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": status >= 400 ? "application/problem+json" : "application/json" },
  });
const problem = (code: string, status: number) => json({ type: `urn:foodplanner:problem:${code}`, title: "x", status }, status);

type Call = { method: string; path: string; query: string; body: unknown };
let isSystemAdmin: boolean;
let active: SupportSession | null;
let calls: Call[];
let startResult: Response | null;

const sessionFor = (mode: "Read" | "Write", minutes = 20): SupportSession => ({
  id: "66666666-6666-6666-6666-666666666666",
  householdId: household,
  householdName: "Hansen",
  mode,
  reasonCategory: "LostOwner",
  startedAt: new Date().toISOString(),
  expiresAt: new Date(Date.now() + minutes * 60_000).toISOString(),
});

beforeEach(() => {
  resetAntiforgeryToken();
  isSystemAdmin = true;
  active = null;
  calls = [];
  startResult = null;
  vi.stubGlobal(
    "fetch",
    vi.fn(async (request: Request) => {
      const url = new URL(request.url);
      const path = url.pathname;
      const method = request.method;
      if (path === "/api/auth/me") return json({ userId: owner, email: "admin@example.test", kidMode: null, isSystemAdmin });
      if (path === "/api/auth/antiforgery") return json({ token: "t" });
      if (path === "/api/households") return json([]);
      const body = method === "GET" || method === "DELETE" ? null : await request.json().catch(() => null);
      calls.push({ method, path, query: url.search, body });
      if (path === "/api/support/sessions/current") {
        if (method === "DELETE") {
          active = null;
          return new Response(null, { status: 204 });
        }
        return active ? json(active) : problem("support-session-required", 404);
      }
      if (path === "/api/support/sessions" && method === "POST") {
        if (startResult) return startResult;
        const b = body as { mode: "Read" | "Write" };
        active = sessionFor(b.mode);
        return json(active, 201);
      }
      if (path === "/api/support/households") {
        return json({ items: [{ id: household, name: "Hansen", createdAt: "2026-01-02T00:00:00Z" }], page: 1, pageSize: 20, total: 1 });
      }
      if (path === "/api/support/users") {
        return json([{ userId: owner, email: "ola@example.test", emailConfirmed: true, households: [{ id: household, name: "Hansen", role: "Adult" }] }]);
      }
      const base = `/api/support/households/${household}`;
      if (path === base) {
        return json({ id: household, name: "Hansen", createdAt: "2026-01-02T00:00:00Z", plan: null, memberCount: 2, ownerCount: 1, openInviteCount: 1, profileCount: 0, recipeCount: 3, mealPlanCount: 1 });
      }
      if (path === `${base}/members` && method === "GET") {
        return json([
          { userId: owner, email: "owner@example.test", role: "Owner", joinedAt: "2026-01-02T00:00:00Z", pinLocked: false, pinLockedUntil: null },
          { userId: adult, email: "mia@example.test", role: "Adult", joinedAt: "2026-01-03T00:00:00Z", pinLocked: true, pinLockedUntil: new Date(Date.now() + 120_000).toISOString() },
        ]);
      }
      if (path === `${base}/members/${adult}/role` || path === `${base}/members/${adult}/pin-unlock` || path === `${base}/invites/${inviteId}`) {
        return path.endsWith("/role") ? json({ userId: adult, email: "mia@example.test", role: "Owner", joinedAt: "2026-01-03T00:00:00Z" }) : new Response(null, { status: 204 });
      }
      if (path === `${base}/invites`) {
        return json([
          { id: inviteId, role: "Adult", status: "Open", createdAt: "2026-02-01T00:00:00Z", expiresAt: "2026-02-08T00:00:00Z", acceptedAt: null, createdByUserId: owner },
          { id: "77777777-7777-7777-7777-777777777777", role: "Child", status: "Accepted", createdAt: "2026-01-01T00:00:00Z", expiresAt: "2026-01-08T00:00:00Z", acceptedAt: "2026-01-02T00:00:00Z", createdByUserId: owner },
        ]);
      }
      if (path.startsWith(`${base}/meal-plans/`) && !path.endsWith("/lock")) {
        return json({ id: "88888888-8888-8888-8888-888888888888", weekStart: path.split("/")[6], entries: [], locked: true, lockedAt: "2026-03-01T00:00:00Z", lockedBy: null });
      }
      if (path.endsWith("/lock")) return new Response(null, { status: 204 });
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
    <MemoryRouter initialEntries={["/support"]}>
      <SessionProvider>
        <SupportPage />
      </SessionProvider>
    </MemoryRouter>,
  );
}

async function startSession(user: ReturnType<typeof userEvent.setup>, mode: "Read" | "Write") {
  await user.click(await screen.findByRole("button", { name: t("support.households.choose", { name: "Hansen" }) }));
  if (mode === "Write") {
    await user.click(screen.getByRole("radio", { name: t("support.start.write") }));
    await user.type(screen.getByLabelText(t("support.start.password")), "correct horse battery");
  }

  await user.type(screen.getByLabelText(t("support.start.note")), "The only owner left the household");
  await user.click(screen.getByRole("button", { name: t("support.start.button") }));
  await screen.findByTestId("support-banner");
}

describe("who sees the support page", () => {
  it("shows a plain not-found to an account that is not a system admin and asks the API for nothing", async () => {
    isSystemAdmin = false;
    renderPage();

    expect(await screen.findByRole("heading", { name: t("support.notFound.title") })).toBeInTheDocument();
    expect(screen.queryByText(t("support.start.title"))).not.toBeInTheDocument();
    expect(calls.filter((c) => c.path.startsWith("/api/support"))).toEqual([]);
  });

  it("shows the start form and every household, paged, to a system admin without a session", async () => {
    renderPage();

    expect(await screen.findByRole("heading", { name: t("support.title") })).toBeInTheDocument();
    expect(await screen.findByTestId("support-household-row")).toHaveTextContent("Hansen");
    expect(calls.find((c) => c.path === "/api/support/households")?.query).toBe("?page=1&pageSize=20");
    expect(screen.getByRole("button", { name: t("support.start.button") })).toBeDisabled(); // no household chosen yet
  });
});

describe("starting and ending a session", () => {
  it("sends the household, mode, category and note, then shows the banner with the end button", async () => {
    const user = userEvent.setup();
    renderPage();
    await startSession(user, "Read");

    const start = calls.find((c) => c.method === "POST" && c.path === "/api/support/sessions");
    expect(start?.body).toEqual({ householdId: household, mode: "Read", reasonCategory: "LostOwner", reasonNote: "The only owner left the household", password: null });
    expect(screen.getByTestId("support-banner")).toHaveTextContent("Hansen");

    await user.click(screen.getByRole("button", { name: t("support.banner.end") }));
    expect(await screen.findByRole("heading", { name: t("support.start.title") })).toBeInTheDocument();
    expect(calls.some((c) => c.method === "DELETE" && c.path === "/api/support/sessions/current")).toBe(true);
  });

  it("asks for the password in write mode and sends it", async () => {
    const user = userEvent.setup();
    renderPage();
    await startSession(user, "Write");

    const start = calls.find((c) => c.method === "POST" && c.path === "/api/support/sessions");
    expect(start?.body).toMatchObject({ mode: "Write", password: "correct horse battery" });
  });

  it("shows the translated message when the password is wrong", async () => {
    startResult = problem("password-incorrect", 403);
    const user = userEvent.setup();
    renderPage();
    await user.click(await screen.findByRole("button", { name: t("support.households.choose", { name: "Hansen" }) }));
    await user.click(screen.getByRole("radio", { name: t("support.start.write") }));
    await user.type(screen.getByLabelText(t("support.start.password")), "nope");
    await user.type(screen.getByLabelText(t("support.start.note")), "The only owner left the household");
    await user.click(screen.getByRole("button", { name: t("support.start.button") }));

    expect(await screen.findByRole("alert")).toHaveTextContent(t("support.problem.passwordIncorrect"));
  });

  it("resumes an open session after a reload", async () => {
    active = sessionFor("Read");
    renderPage();

    expect(await screen.findByTestId("support-banner")).toHaveTextContent("Hansen");
    expect(screen.queryByText(t("support.start.title"))).not.toBeInTheDocument();
  });

  it("treats an unreachable API as a failure with a retry", async () => {
    vi.stubGlobal("fetch", vi.fn(async (request: Request) => {
      const path = new URL(request.url).pathname;
      if (path === "/api/auth/me") return json({ userId: owner, email: "admin@example.test", kidMode: null, isSystemAdmin: true });
      if (path === "/api/auth/antiforgery") return json({ token: "t" });
      if (path === "/api/households") return json([]);
      throw new TypeError("network down");
    }));
    renderPage();

    expect(await screen.findByRole("alert")).toHaveTextContent(t("problem.unreachable"));
    expect(screen.getByRole("button", { name: t("support.retry") })).toBeInTheDocument();
  });
});

describe("what a session shows and which buttons it offers", () => {
  it("offers no write button in a read session", async () => {
    active = sessionFor("Read");
    const user = userEvent.setup();
    renderPage();
    await screen.findByTestId("support-banner");

    await user.click(screen.getByRole("button", { name: t("support.tab.members") }));
    const rows = await screen.findAllByTestId("support-member-row");
    expect(rows).toHaveLength(2);
    expect(within(rows[1]!).getByText(/mia@example.test/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: t("support.members.makeOwner") })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: t("support.members.clearPin") })).not.toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: t("support.tab.invites") }));
    await screen.findAllByTestId("support-invite-row");
    expect(screen.queryByRole("button", { name: t("support.invites.cancel") })).not.toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: t("support.tab.plan") }));
    await screen.findByTestId("support-week-locked");
    expect(screen.queryByRole("button", { name: t("support.plan.unlock") })).not.toBeInTheDocument();
  });

  it("shows the overview with the plan field empty for now", async () => {
    active = sessionFor("Read");
    renderPage();

    expect(await screen.findByTestId("support-plan")).toHaveTextContent(t("support.overview.noPlan"));
  });

  it("confirms with the household and the person named before making an Owner, then sends the role", async () => {
    active = sessionFor("Write");
    const user = userEvent.setup();
    renderPage();
    await screen.findByTestId("support-banner");
    await user.click(screen.getByRole("button", { name: t("support.tab.members") }));
    await screen.findAllByTestId("support-member-row");

    await user.click(screen.getByRole("button", { name: t("support.members.makeOwnerFor", { email: "mia@example.test" }) }));

    const dialog = await screen.findByRole("dialog");
    expect(dialog).toHaveTextContent(t("support.confirm.promote", { email: "mia@example.test", household: "Hansen" }));
    expect(calls.some((c) => c.method === "PUT")).toBe(false); // nothing is sent before the confirmation
    await user.click(within(dialog).getByRole("button", { name: t("support.confirm.yes") }));

    await waitFor(() => expect(calls.find((c) => c.method === "PUT")).toBeDefined());
    expect(calls.find((c) => c.method === "PUT")).toMatchObject({ path: `/api/support/households/${household}/members/${adult}/role`, body: { role: "Owner" } });
    expect(await screen.findByText(t("support.done"))).toBeInTheDocument();
  });

  it("confirms before clearing a PIN lock, cancelling an invite and unlocking a week", async () => {
    active = sessionFor("Write");
    const user = userEvent.setup();
    renderPage();
    await screen.findByTestId("support-banner");

    await user.click(screen.getByRole("button", { name: t("support.tab.members") }));
    await user.click(await screen.findByRole("button", { name: t("support.members.clearPinFor", { email: "mia@example.test" }) }));
    expect(await screen.findByRole("dialog")).toHaveTextContent(t("support.confirm.clearPin", { email: "mia@example.test", household: "Hansen" }));
    await user.click(screen.getByRole("button", { name: t("support.confirm.yes") }));
    await waitFor(() => expect(calls.some((c) => c.method === "POST" && c.path.endsWith("/pin-unlock"))).toBe(true));

    await user.click(screen.getByRole("button", { name: t("support.tab.invites") }));
    const rows = await screen.findAllByTestId("support-invite-row");
    expect(within(rows[1]!).queryByRole("button")).not.toBeInTheDocument(); // an accepted invite cannot be cancelled
    await user.click(within(rows[0]!).getByRole("button"));
    await user.click(await screen.findByRole("button", { name: t("support.confirm.yes") }));
    await waitFor(() => expect(calls.some((c) => c.method === "DELETE" && c.path.endsWith(`/invites/${inviteId}`))).toBe(true));

    await user.click(screen.getByRole("button", { name: t("support.tab.plan") }));
    await user.click(await screen.findByRole("button", { name: t("support.plan.unlock") }));
    await user.click(await screen.findByRole("button", { name: t("support.confirm.yes") }));
    await waitFor(() => expect(calls.some((c) => c.method === "DELETE" && c.path.endsWith("/lock"))).toBe(true));
  });

  it("shows the translated message when a write is refused", async () => {
    active = sessionFor("Write");
    const user = userEvent.setup();
    renderPage();
    await screen.findByTestId("support-banner");
    await user.click(screen.getByRole("button", { name: t("support.tab.members") }));
    await user.click(await screen.findByRole("button", { name: t("support.members.makeOwnerFor", { email: "mia@example.test" }) }));
    vi.stubGlobal("fetch", vi.fn(async (request: Request) => {
      const path = new URL(request.url).pathname;
      if (path === "/api/auth/antiforgery") return json({ token: "t" });
      return problem("member-not-found", 404);
    }));

    await user.click(await screen.findByRole("button", { name: t("support.confirm.yes") }));

    expect(await within(await screen.findByRole("dialog")).findByRole("alert")).toHaveTextContent(t("household.problem.memberNotFound"));
  });
});
