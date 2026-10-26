import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes, useLocation } from "react-router";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { SessionProvider } from "../session";
import { t } from "../test-i18n";
import Invite from "./invite";
import Shell from "./shell";

const me = { userId: "11111111-1111-1111-1111-111111111111", email: "member@example.test" };

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": status >= 400 ? "application/problem+json" : "application/json" },
  });
const problem = (status: number, code: string) => json({ status, title: "x", type: `urn:foodplanner:problem:${code}` }, status);

type Server = { signedIn: boolean; households: { id: string; name: string; role: string }[]; accept: Response | null; accepted: string[] };
let server: Server;

beforeEach(() => {
  server = { signedIn: true, households: [], accept: null, accepted: [] };
  vi.stubGlobal(
    "fetch",
    vi.fn(async (request: Request) => {
      const path = new URL(request.url).pathname;
      if (path === "/api/auth/me") return server.signedIn ? json(me) : problem(401, "unauthenticated");
      if (path === "/api/auth/antiforgery") return json({ token: "t" });
      if (path === "/api/households") return json(server.households);
      const accept = path.match(/^\/api\/invites\/([^/]+)\/accept$/);
      if (accept && request.method === "POST") {
        server.accepted.push(accept[1]!);
        if (server.accept) return server.accept;
        server.households = [{ id: "h9", name: "Cabin", role: "Adult" }];
        return json({ householdId: "h9", role: "Adult" });
      }
      throw new Error(`unexpected ${request.method} ${path}`);
    }),
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
  localStorage.clear();
});

function Where() {
  const location = useLocation();
  return <output data-testid="url">{location.pathname + location.search}</output>;
}

function renderInvite(url = "/invite/tok_abc-123") {
  const user = userEvent.setup();
  render(
    <MemoryRouter initialEntries={[url]}>
      <SessionProvider>
        <Where />
        <Routes>
          <Route path="login" element={<p>login page</p>} />
          <Route element={<Shell />}>
            <Route path="invite/:token" element={<Invite />} />
            <Route path="household" element={<p>household page</p>} />
          </Route>
        </Routes>
      </SessionProvider>
    </MemoryRouter>,
  );
  return user;
}

describe("accepting an invitation", () => {
  it("does not join until the button is pressed", async () => {
    renderInvite();
    expect(await screen.findByRole("heading", { name: t("invite.title") })).toBeInTheDocument();
    expect(server.accepted).toEqual([]);
  });

  it("joins with the token from the link, shows the role and makes the household active", async () => {
    const user = renderInvite();
    await user.click(await screen.findByRole("button", { name: t("invite.accept") }));

    expect(await screen.findByText(t("invite.joined", { role: t("household.role.Adult") }))).toBeInTheDocument();
    expect(server.accepted).toEqual(["tok_abc-123"]);
    // The session was refreshed: the shell now shows the new household.
    await waitFor(() => expect(screen.getByTestId("household-name")).toHaveTextContent("Cabin"));
    expect(screen.getByRole("link", { name: t("invite.open") })).toHaveAttribute("href", "/household");
  });

  it("sends a signed-out visitor to login and back here (returnTo), without joining", async () => {
    server.signedIn = false;
    renderInvite();
    await waitFor(() => expect(screen.getByTestId("url")).toHaveTextContent("/login?returnTo=%2Finvite%2Ftok_abc-123"));
    expect(server.accepted).toEqual([]);
  });

  it.each([
    [410, "invite-invalid", "problem.inviteInvalid"],
    [409, "already-member", "problem.alreadyMember"],
    [429, "rate-limited", "problem.rateLimited"],
  ] as const)("shows a translated message for %i %s", async (status, code, key) => {
    server.accept = problem(status, code);
    const user = renderInvite();
    await user.click(await screen.findByRole("button", { name: t("invite.accept") }));
    expect(await screen.findByRole("alert")).toHaveTextContent(t(key));
    // The button is usable again (a 429 can be retried) and no household was added.
    expect(screen.getByRole("button", { name: t("invite.accept") })).toBeEnabled();
  });
});
