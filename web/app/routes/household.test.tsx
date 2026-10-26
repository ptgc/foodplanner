import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { components } from "../api/schema";
import { SessionProvider } from "../session";
import { t } from "../test-i18n";
import Household from "./household";

type Role = components["schemas"]["HouseholdRole"];
type Member = components["schemas"]["MemberResponse"];
type OpenInvite = components["schemas"]["OpenInviteResponse"];
type HouseholdRow = components["schemas"]["HouseholdResponse"];

const me = { userId: "11111111-1111-1111-1111-111111111111", email: "owner@example.test" };
const anna: Member = { userId: "22222222-2222-2222-2222-222222222222", email: "anna@example.test", role: "Adult", joinedAt: "2031-03-04T10:00:00Z" };
const kid: Member = { userId: "33333333-3333-3333-3333-333333333333", email: "kid@example.test", role: "Child", joinedAt: "2031-03-05T10:00:00Z" };
const co: Member = { userId: "44444444-4444-4444-4444-444444444444", email: "co@example.test", role: "Owner", joinedAt: "2031-03-06T10:00:00Z" };
const myRow = (role: Role): Member => ({ ...me, role, joinedAt: "2031-03-01T10:00:00Z" });
const invite1: OpenInvite = {
  id: "aaaaaaaa-0000-0000-0000-000000000001",
  role: "Child",
  expiresAt: "2031-03-20T10:00:00Z",
  createdAt: "2031-03-13T10:00:00Z",
  createdByUserId: me.userId,
};

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": status >= 400 ? "application/problem+json" : "application/json" },
  });
const problem = (status: number, code?: string) =>
  json({ status, title: "x", ...(code ? { type: `urn:foodplanner:problem:${code}` } : {}) }, status);

type Call = { method: string; path: string; body: unknown };
type Server = {
  households: HouseholdRow[];
  members: Member[];
  invites: OpenInvite[];
  calls: Call[];
  /** The next request with this "METHOD /path" answers with this response instead. */
  failures: Map<string, Response>;
  exportText: string;
};
let server: Server;

const callsTo = (method: string, pattern: RegExp) => server.calls.filter((c) => c.method === method && pattern.test(c.path));

function setServer(role: Role, extra: Partial<Server> = {}) {
  server = {
    households: [{ id: "h1", name: "Home", role }],
    members: [myRow(role), anna, kid],
    invites: [invite1],
    calls: [],
    failures: new Map(),
    exportText: '{"format":"foodplanner-household-export"}',
    ...extra,
  };
}

beforeEach(() => {
  setServer("Owner");
  vi.stubGlobal(
    "fetch",
    vi.fn(async (request: Request) => {
      const url = new URL(request.url);
      const path = url.pathname;
      const method = request.method;
      const body = method === "GET" || method === "DELETE" || method === "HEAD" ? null : await request.json().catch(() => null);
      server.calls.push({ method, path, body });
      const failure = server.failures.get(`${method} ${path}`);
      if (failure) {
        server.failures.delete(`${method} ${path}`);
        return failure;
      }
      if (path === "/api/auth/me") return json(me);
      if (path === "/api/auth/antiforgery") return json({ token: "t" });
      if (path === "/api/households" && method === "GET") return json(server.households);
      if (path === "/api/households" && method === "POST") {
        const created = { id: "h-new", name: (body as { name: string }).name, role: "Owner" as Role };
        server.households.push(created);
        return json(created, 201);
      }
      const m = path.match(/^\/api\/households\/([^/]+)(\/.*)?$/);
      if (m) {
        const rest = m[2] ?? "";
        if (rest === "/members" && method === "GET") return json(server.members);
        if (rest === "/invites" && method === "GET") return json(server.invites);
        if (rest === "/invites" && method === "POST") {
          return json({ token: "tok_abc-123", expiresAt: "2031-03-20T10:00:00Z", role: (body as { role: Role }).role }, 201);
        }
        const roleMatch = rest.match(/^\/members\/([^/]+)\/role$/);
        if (roleMatch && method === "PUT") {
          const member = server.members.find((x) => x.userId === roleMatch[1])!;
          member.role = (body as { role: Role }).role;
          return json(member);
        }
        const removeMatch = rest.match(/^\/members\/([^/]+)$/);
        if (removeMatch && method === "DELETE") {
          server.members = server.members.filter((x) => x.userId !== removeMatch[1]);
          return new Response(null, { status: 204 });
        }
        const inviteMatch = rest.match(/^\/invites\/([^/]+)$/);
        if (inviteMatch && method === "DELETE") {
          server.invites = server.invites.filter((x) => x.id !== inviteMatch[1]);
          return new Response(null, { status: 204 });
        }
        if (rest === "/leave" && method === "POST") {
          server.households = server.households.filter((h) => h.id !== m[1]);
          return new Response(null, { status: 204 });
        }
        if (rest === "" && method === "DELETE") {
          server.households = server.households.filter((h) => h.id !== m[1]);
          return new Response(null, { status: 204 });
        }
        if (rest === "/export" && method === "GET") {
          return new Response(server.exportText, { status: 200, headers: { "content-type": "application/json" } });
        }
      }
      throw new Error(`unexpected ${method} ${path}`);
    }),
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
  localStorage.clear();
});

function renderPage() {
  const user = userEvent.setup();
  render(
    <MemoryRouter initialEntries={["/household"]}>
      <SessionProvider>
        <Household />
      </SessionProvider>
    </MemoryRouter>,
  );
  return user;
}

const button = (name: string) => screen.getByRole("button", { name });
const queryButton = (name: string) => screen.queryByRole("button", { name });
const removeFor = (email: string) => button(t("household.members.removeFor", { email }));

async function renderOwner() {
  const user = renderPage();
  await screen.findByTestId("member-list");
  return user;
}

describe("role based controls", () => {
  it("shows an Owner the household, members with role selects and Remove, invitations, export, leave and delete", async () => {
    await renderOwner();

    expect(screen.getByTestId("household-settings-name")).toHaveTextContent("Home");
    expect(screen.getByTestId("household-role")).toHaveTextContent(t("household.role.Owner"));
    const rows = within(screen.getByTestId("member-list")).getAllByTestId("member-row");
    expect(rows).toHaveLength(3);
    expect(rows[0]).toHaveTextContent(t("household.members.you"));
    // Yourself: no select and no Remove (leave is the way out).
    expect(within(rows[0]!).queryByRole("combobox")).toBeNull();
    expect(within(rows[0]!).queryByRole("button")).toBeNull();
    expect(screen.getByRole("combobox", { name: t("household.members.roleOf", { email: anna.email! }) })).toHaveValue("Adult");
    expect(removeFor(anna.email!)).toBeInTheDocument();
    expect(button(t("household.invites.create"))).toBeInTheDocument();
    expect(screen.getByTestId("invite-list")).toBeInTheDocument();
    expect(button(t("household.export.button"))).toBeInTheDocument();
    expect(button(t("household.leave.button"))).toBeInTheDocument();
    expect(button(t("household.delete.button"))).toBeInTheDocument();
  });

  it("does not offer Remove for another Owner (the API refuses it)", async () => {
    setServer("Owner", { members: [myRow("Owner"), co] });
    await renderOwner();
    expect(screen.getByRole("combobox", { name: t("household.members.roleOf", { email: co.email! }) })).toHaveValue("Owner");
    expect(queryButton(t("household.members.removeFor", { email: co.email! }))).toBeNull();
  });

  it("shows an Adult only the household, export and leave: no members, invitations or delete, and never asks for them", async () => {
    setServer("Adult");
    renderPage();
    expect(await screen.findByTestId("household-role")).toHaveTextContent(t("household.role.Adult"));

    expect(screen.getByText(t("household.ownerOnlyNote"))).toBeInTheDocument();
    expect(screen.queryByTestId("member-list")).toBeNull();
    expect(screen.queryByTestId("invite-list")).toBeNull();
    expect(queryButton(t("household.invites.create"))).toBeNull();
    expect(queryButton(t("household.delete.button"))).toBeNull();
    expect(button(t("household.export.button"))).toBeInTheDocument();
    expect(button(t("household.leave.button"))).toBeInTheDocument();
    expect(server.calls.filter((c) => /\/(members|invites)$/.test(c.path))).toEqual([]);
  });

  it("shows a Child only the household and leave: no export, members, invitations or delete", async () => {
    setServer("Child");
    renderPage();
    expect(await screen.findByTestId("household-role")).toHaveTextContent(t("household.role.Child"));

    expect(queryButton(t("household.export.button"))).toBeNull();
    expect(queryButton(t("household.delete.button"))).toBeNull();
    expect(queryButton(t("household.invites.create"))).toBeNull();
    expect(screen.queryByTestId("member-list")).toBeNull();
    expect(button(t("household.leave.button"))).toBeInTheDocument();
  });

  it("shows the translated 403 if the API refuses the member list anyway", async () => {
    server.failures.set("GET /api/households/h1/members", problem(403, "forbidden"));
    renderPage();
    expect(await screen.findByText(t("problem.forbidden"))).toBeInTheDocument();
  });
});

describe("member roles and removal", () => {
  it("changes a role with PUT and says so", async () => {
    const user = await renderOwner();
    await user.selectOptions(screen.getByRole("combobox", { name: t("household.members.roleOf", { email: anna.email! }) }), "Child");

    await waitFor(() => expect(callsTo("PUT", /\/members\/.+\/role$/)).toHaveLength(1));
    expect(callsTo("PUT", /\/role$/)[0]).toMatchObject({ path: `/api/households/h1/members/${anna.userId}/role`, body: { role: "Child" } });
    expect(await screen.findByTestId("household-notice")).toHaveTextContent(
      t("household.members.roleChanged", { email: anna.email!, role: t("household.role.Child") }),
    );
  });

  it("explains that the last Owner cannot be demoted", async () => {
    setServer("Owner", { members: [myRow("Owner"), co] });
    server.failures.set(`PUT /api/households/h1/members/${co.userId}/role`, problem(409, "last-owner"));
    const user = await renderOwner();
    await user.selectOptions(screen.getByRole("combobox", { name: t("household.members.roleOf", { email: co.email! }) }), "Adult");
    expect(await screen.findByText(t("household.problem.lastOwnerDemote"))).toBeInTheDocument();
  });

  it("asks before removing: Cancel and Escape do nothing, and focus returns to the button", async () => {
    const user = await renderOwner();
    await user.click(removeFor(anna.email!));

    const dialog = screen.getByRole("dialog", { name: t("household.members.removeTitle", { email: anna.email! }) });
    expect(dialog).toHaveAttribute("aria-modal", "true");
    expect(within(dialog).getByText(t("household.members.removeBody"))).toBeInTheDocument();
    expect(within(dialog).getByRole("button", { name: t("household.dialog.cancel") })).toHaveFocus();

    await user.click(within(dialog).getByRole("button", { name: t("household.dialog.cancel") }));
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(removeFor(anna.email!)).toHaveFocus();

    await user.click(removeFor(anna.email!));
    await user.keyboard("{Escape}");
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(callsTo("DELETE", /members/)).toEqual([]);
  });

  it("keeps Tab inside the dialog", async () => {
    const user = await renderOwner();
    await user.click(removeFor(anna.email!));
    const dialog = screen.getByRole("dialog");
    const cancel = within(dialog).getByRole("button", { name: t("household.dialog.cancel") });
    const confirm = within(dialog).getByRole("button", { name: t("household.members.removeConfirm") });
    expect(cancel).toHaveFocus();
    await user.tab();
    expect(confirm).toHaveFocus();
    await user.tab();
    expect(cancel).toHaveFocus();
    await user.tab({ shift: true });
    expect(confirm).toHaveFocus();
  });

  it("removes the member with DELETE after confirmation", async () => {
    const user = await renderOwner();
    await user.click(removeFor(anna.email!));
    await user.click(button(t("household.members.removeConfirm")));

    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(callsTo("DELETE", /members/)).toEqual([
      expect.objectContaining({ path: `/api/households/h1/members/${anna.userId}` }),
    ]);
    expect(screen.queryByText(anna.email!)).toBeNull();
    expect(screen.getByTestId("household-notice")).toHaveTextContent(t("household.members.removed", { email: anna.email! }));
  });

  it("maps member-not-found to a translated message in the dialog and reloads the list", async () => {
    const user = await renderOwner();
    server.failures.set(`DELETE /api/households/h1/members/${anna.userId}`, problem(404, "member-not-found"));
    await user.click(removeFor(anna.email!));
    await user.click(button(t("household.members.removeConfirm")));

    expect(await screen.findByText(t("household.problem.memberNotFound"))).toBeInTheDocument();
    expect(screen.getByRole("dialog")).toBeInTheDocument();
    await waitFor(() => expect(callsTo("GET", /\/members$/).length).toBeGreaterThan(1));
  });
});

describe("invitations", () => {
  it("creates an invitation, shows the link and copies it", async () => {
    const user = await renderOwner();
    await user.selectOptions(screen.getByLabelText(t("household.invites.role")), "Child");
    await user.click(button(t("household.invites.create")));

    expect(callsTo("POST", /\/invites$/)[0]?.body).toEqual({ role: "Child" });
    const link = await screen.findByTestId("invite-link");
    expect(link).toHaveValue(`${window.location.origin}/invite/tok_abc-123`);

    await user.click(button(t("household.invites.copy")));
    expect(await navigator.clipboard.readText()).toBe(`${window.location.origin}/invite/tok_abc-123`);
    expect(await screen.findByText(t("household.invites.copied"))).toBeInTheDocument();
  });

  it("offers Share when the browser can share, with the link in the text", async () => {
    const share = vi.fn(async () => undefined);
    Object.defineProperty(navigator, "share", { value: share, configurable: true });
    const user = await renderOwner();
    await user.click(button(t("household.invites.create")));
    await screen.findByTestId("invite-link");
    await user.click(button(t("household.invites.share")));
    expect(share).toHaveBeenCalledTimes(1);
    expect(JSON.stringify(share.mock.calls[0])).toContain("/invite/tok_abc-123");
    Reflect.deleteProperty(navigator, "share");
  });

  it("refuses to create for a role the API refuses with the translated 403", async () => {
    server.failures.set("POST /api/households/h1/invites", problem(403, "forbidden"));
    const user = await renderOwner();
    await user.click(button(t("household.invites.create")));
    expect(await screen.findByText(t("problem.forbidden"))).toBeInTheDocument();
    expect(screen.queryByTestId("invite-link")).toBeNull();
  });

  it("lists open invitations with expiry and revokes only after confirmation", async () => {
    const user = await renderOwner();
    const row = await screen.findByTestId("invite-row");
    expect(row).toHaveTextContent(t("household.role.Child"));
    const revoke = within(row).getByRole("button", { name: new RegExp(t("household.invites.revoke")) });

    await user.click(revoke);
    await user.click(button(t("household.dialog.cancel")));
    expect(callsTo("DELETE", /invites/)).toEqual([]);
    expect(screen.getByTestId("invite-row")).toBeInTheDocument();

    await user.click(revoke);
    await user.click(button(t("household.invites.revokeConfirm")));
    await waitFor(() => expect(screen.queryByTestId("invite-row")).toBeNull());
    expect(callsTo("DELETE", /invites/)).toEqual([expect.objectContaining({ path: `/api/households/h1/invites/${invite1.id}` })]);
    expect(screen.getByText(t("household.invites.none"))).toBeInTheDocument();
  });

  it("maps invite-not-found on revoke", async () => {
    const user = await renderOwner();
    server.failures.set(`DELETE /api/households/h1/invites/${invite1.id}`, problem(404, "invite-not-found"));
    await user.click(within(await screen.findByTestId("invite-row")).getByRole("button"));
    await user.click(button(t("household.invites.revokeConfirm")));
    expect(await screen.findByText(t("household.problem.inviteNotFound"))).toBeInTheDocument();
  });
});

describe("leaving, deleting, exporting", () => {
  it("leaves only after confirmation, then shows the next household", async () => {
    setServer("Adult", {
      households: [
        { id: "h1", name: "Home", role: "Adult" },
        { id: "h2", name: "Cabin", role: "Owner" },
      ],
    });
    const user = renderPage();
    await user.click(await screen.findByRole("button", { name: t("household.leave.button") }));
    const dialog = screen.getByRole("dialog", { name: t("household.leave.confirmTitle", { name: "Home" }) });
    await user.click(within(dialog).getByRole("button", { name: t("household.dialog.cancel") }));
    expect(callsTo("POST", /leave/)).toEqual([]);

    await user.click(button(t("household.leave.button")));
    await user.click(within(screen.getByRole("dialog")).getByRole("button", { name: t("household.leave.confirm") }));

    await waitFor(() => expect(screen.getByTestId("household-settings-name")).toHaveTextContent("Cabin"));
    expect(callsTo("POST", /leave/)).toEqual([expect.objectContaining({ path: "/api/households/h1/leave" })]);
    expect(screen.getByTestId("household-notice")).toHaveTextContent(t("household.leave.done", { name: "Home" }));
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("shows the empty state after leaving the only household", async () => {
    setServer("Child");
    const user = renderPage();
    await user.click(await screen.findByRole("button", { name: t("household.leave.button") }));
    await user.click(within(screen.getByRole("dialog")).getByRole("button", { name: t("household.leave.confirm") }));
    expect(await screen.findByText(t("household.noneTitle"))).toBeInTheDocument();
    expect(screen.queryByTestId("household-settings-name")).toBeNull();
    expect(screen.getByRole("button", { name: t("household.create.button") })).toBeInTheDocument();
  });

  it("explains the last-owner refusal and keeps the dialog open", async () => {
    server.failures.set("POST /api/households/h1/leave", problem(409, "last-owner"));
    const user = await renderOwner();
    await user.click(button(t("household.leave.button")));
    await user.click(within(screen.getByRole("dialog")).getByRole("button", { name: t("household.leave.confirm") }));

    expect(await screen.findByText(t("household.problem.lastOwner"))).toBeInTheDocument();
    expect(screen.getByRole("dialog")).toBeInTheDocument();
    expect(screen.getByTestId("household-settings-name")).toHaveTextContent("Home");
  });

  it("deletes only when the household name is typed exactly", async () => {
    const user = await renderOwner();
    await user.click(button(t("household.delete.button")));
    const dialog = screen.getByRole("dialog", { name: t("household.delete.confirmTitle", { name: "Home" }) });
    const confirm = within(dialog).getByRole("button", { name: t("household.delete.confirm") });
    const field = within(dialog).getByLabelText(t("household.delete.typeLabel", { name: "Home" }));
    expect(field).toHaveFocus();
    expect(confirm).toBeDisabled();

    await user.type(field, "Hom");
    expect(confirm).toBeDisabled();
    await user.keyboard("{Enter}");
    expect(callsTo("DELETE", /^\/api\/households\/h1$/)).toEqual([]);

    await user.type(field, "e");
    expect(confirm).toBeEnabled();
    await user.click(confirm);

    expect(await screen.findByText(t("household.noneTitle"))).toBeInTheDocument();
    expect(callsTo("DELETE", /^\/api\/households\/h1$/)).toHaveLength(1);
    expect(screen.getByTestId("household-notice")).toHaveTextContent(t("household.delete.done", { name: "Home" }));
  });

  it("Cancel on the delete dialog calls nothing", async () => {
    const user = await renderOwner();
    await user.click(button(t("household.delete.button")));
    await user.click(button(t("household.dialog.cancel")));
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(callsTo("DELETE", /^\/api\/households\/h1$/)).toEqual([]);
  });

  it("downloads the export as a file", async () => {
    const createObjectURL = vi.fn(() => "blob:export");
    const revokeObjectURL = vi.fn();
    vi.stubGlobal("URL", Object.assign(URL, { createObjectURL, revokeObjectURL }));
    const click = vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(() => undefined);
    setServer("Adult");
    const user = renderPage();
    await user.click(await screen.findByRole("button", { name: t("household.export.button") }));

    await waitFor(() => expect(click).toHaveBeenCalledTimes(1));
    const blob = (createObjectURL.mock.calls[0] as unknown as [Blob])[0];
    expect(await blob.text()).toBe(server.exportText);
    expect(revokeObjectURL).toHaveBeenCalledWith("blob:export");
    expect(await screen.findByTestId("household-notice")).toHaveTextContent(t("household.export.done"));
    click.mockRestore();
  });

  it("maps the export rate limit (429) to a translated message and downloads nothing", async () => {
    const createObjectURL = vi.fn(() => "blob:export");
    vi.stubGlobal("URL", Object.assign(URL, { createObjectURL, revokeObjectURL: vi.fn() }));
    server.failures.set("GET /api/households/h1/export", problem(429, "rate-limited"));
    const user = await renderOwner();
    await user.click(button(t("household.export.button")));
    expect(await screen.findByText(t("household.problem.exportRateLimited"))).toBeInTheDocument();
    expect(createObjectURL).not.toHaveBeenCalled();
  });
});

describe("creating another household", () => {
  it("creates it, makes it the active household and says so", async () => {
    const user = await renderOwner();
    await user.type(screen.getByLabelText(t("household.create.name")), "  Cabin ");
    await user.click(button(t("household.create.button")));

    await waitFor(() => expect(screen.getByTestId("household-settings-name")).toHaveTextContent("Cabin"));
    expect(callsTo("POST", /^\/api\/households$/)[0]?.body).toEqual({ name: "Cabin" });
    expect(screen.getByTestId("household-notice")).toHaveTextContent(t("household.create.done", { name: "Cabin" }));
  });

  it("does not post an empty name", async () => {
    const user = await renderOwner();
    expect(button(t("household.create.button"))).toBeDisabled();
    await user.type(screen.getByLabelText(t("household.create.name")), "   ");
    expect(button(t("household.create.button"))).toBeDisabled();
    expect(callsTo("POST", /^\/api\/households$/)).toEqual([]);
  });
});
