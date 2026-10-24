import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes, useLocation } from "react-router";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { resetAntiforgeryToken } from "../api/client";
import { t } from "../test-i18n";
import { SessionProvider } from "../session";
import Login from "./login";
import Shell from "./shell";

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": status >= 400 ? "application/problem+json" : "application/json" },
  });

const user = { userId: "11111111-1111-1111-1111-111111111111", email: "admin@example.test" };
const home = { id: "h1", name: "Home", role: "Owner" };
const cabin = { id: "h2", name: "Cabin", role: "Adult" };

type Backend = { signedIn: boolean; loginStatus: number; loginType?: string | null; households: unknown[]; cookieWorks: boolean };
let backend: Backend;
let requests: Request[];

beforeEach(() => {
  resetAntiforgeryToken();
  window.localStorage.clear();
  requests = [];
  backend = { signedIn: false, loginStatus: 204, households: [home], cookieWorks: true };
  vi.stubGlobal(
    "fetch",
    vi.fn(async (request: Request) => {
      requests.push(request);
      const path = new URL(request.url).pathname;
      switch (path) {
        case "/api/auth/antiforgery":
          return json({ token: "t" });
        case "/api/auth/login":
          if (backend.loginStatus === 204) backend.signedIn = backend.cookieWorks;
          return backend.loginStatus === 204
            ? new Response(null, { status: 204 })
            : json({ type: backend.loginType !== undefined ? (backend.loginType ?? undefined) : backend.loginStatus === 429 ? "urn:foodplanner:problem:rate-limited" : "urn:foodplanner:problem:login-failed", title: "Login failed", status: backend.loginStatus }, backend.loginStatus);
        case "/api/auth/logout":
          backend.signedIn = false;
          return new Response(null, { status: 204 });
        case "/api/auth/me":
          return backend.signedIn ? json(user) : json({ title: "Unauthenticated", status: 401 }, 401);
        case "/api/households":
          return json(backend.households);
        default:
          return json({ status: "Healthy", database: "Up" });
      }
    }),
  );
});
afterEach(() => vi.unstubAllGlobals());

function Where() {
  const l = useLocation();
  return <p data-testid="where">{l.pathname + l.search}</p>;
}

function renderApp(initial: string) {
  return render(
    <MemoryRouter initialEntries={[initial]}>
      <SessionProvider>
        <Where />
        <Routes>
          <Route path="login" element={<Login />} />
          <Route element={<Shell />}>
            <Route index element={<h1>Start</h1>} />
            <Route path="recipes" element={<h1>Recipes page</h1>} />
          </Route>
        </Routes>
      </SessionProvider>
    </MemoryRouter>,
  );
}

async function fillAndSubmit(email = "admin@example.test", password = "pw") {
  await userEvent.type(await screen.findByLabelText(t("login.email")), email);
  await userEvent.type(screen.getByLabelText(t("login.password")), password);
  await userEvent.click(screen.getByRole("button", { name: t("common.signIn") }));
}

describe("route guard and login", () => {
  it("redirects signed-out users to /login with returnTo, then back after sign-in", async () => {
    renderApp("/recipes?x=1");
    await waitFor(() => expect(screen.getByTestId("where")).toHaveTextContent("/login?returnTo=%2Frecipes%3Fx%3D1"));
    await fillAndSubmit();
    expect(await screen.findByRole("heading", { name: "Recipes page" })).toBeInTheDocument();
    expect(screen.getByTestId("where")).toHaveTextContent("/recipes?x=1");
    expect(screen.getByTestId("user-email")).toHaveTextContent("admin@example.test");
    expect(screen.getByTestId("household-name")).toHaveTextContent("Home");
  });

  it("sends the antiforgery header on login and logout, with a fresh token after login", async () => {
    renderApp("/login");
    await fillAndSubmit();
    await screen.findByRole("heading", { name: "Start" });
    await userEvent.click(screen.getByRole("button", { name: t("shell.logOut") }));
    await screen.findByRole("heading", { name: t("common.signIn") });
    const posts = requests.filter((r) => r.method === "POST");
    expect(posts.map((r) => new URL(r.url).pathname)).toEqual(["/api/auth/login", "/api/auth/logout"]);
    expect(posts.every((r) => r.headers.get("X-XSRF-TOKEN") === "t")).toBe(true);
    // Token requested once for login and again (fresh) for logout.
    expect(requests.filter((r) => new URL(r.url).pathname === "/api/auth/antiforgery")).toHaveLength(2);
  });

  it("ignores an unsafe returnTo", async () => {
    renderApp("/login?returnTo=https%3A%2F%2Fevil.example");
    await fillAndSubmit();
    await screen.findByRole("heading", { name: "Start" });
    expect(screen.getByTestId("where")).toHaveTextContent(/^\/$/);
  });

  it("shows the generic error on failure and stays on /login", async () => {
    backend.loginStatus = 401;
    renderApp("/login");
    await fillAndSubmit("nobody@example.test", "wrong");
    expect(await screen.findByRole("alert")).toHaveTextContent(t("problem.loginFailed"));
    expect(screen.getByTestId("where")).toHaveTextContent("/login");
    expect(screen.getByRole("button", { name: t("common.signIn") })).toBeEnabled();
  });

  it("shows a rate-limit message on 429", async () => {
    backend.loginStatus = 429;
    renderApp("/login");
    await fillAndSubmit();
    expect(await screen.findByRole("alert")).toHaveTextContent(t("problem.rateLimited"));
  });

  it("maps the ProblemDetails type, not the status, and falls back to the status without a type", async () => {
    backend.loginStatus = 403;
    backend.loginType = "urn:foodplanner:problem:antiforgery-invalid";
    renderApp("/login");
    await fillAndSubmit();
    expect(await screen.findByRole("alert")).toHaveTextContent(t("problem.antiforgeryInvalid"));

    backend.loginStatus = 429;
    backend.loginType = null; // a proxy answered with a bare 429
    await userEvent.click(screen.getByRole("button", { name: t("common.signIn") }));
    await waitFor(() => expect(screen.getByRole("alert")).toHaveTextContent(t("problem.rateLimited")));
  });

  it("shows the generic message for an unknown problem type", async () => {
    backend.loginStatus = 500;
    backend.loginType = "urn:foodplanner:problem:from-the-future";
    renderApp("/login");
    await fillAndSubmit();
    expect(await screen.findByRole("alert")).toHaveTextContent(t("problem.generic"));
  });

  it("shows the Safari hint only when login succeeds but /me is 401", async () => {
    backend.cookieWorks = false;
    renderApp("/login");
    await fillAndSubmit();
    expect(await screen.findByRole("alert")).toHaveTextContent(t("login.cookieMissing"));
  });

  it("disables the button while submitting", async () => {
    renderApp("/login");
    await userEvent.type(await screen.findByLabelText(t("login.email")), "a@b.c");
    await userEvent.type(screen.getByLabelText(t("login.password")), "pw");
    const click = userEvent.click(screen.getByRole("button", { name: t("common.signIn") }));
    expect(await screen.findByRole("button", { name: t("login.signingIn") })).toBeDisabled();
    await click;
  });
});

describe("shell", () => {
  it("stays signed in on load and logs out back to /login", async () => {
    backend.signedIn = true;
    renderApp("/");
    expect(await screen.findByTestId("user-email")).toHaveTextContent("admin@example.test");
    await userEvent.click(screen.getByRole("button", { name: t("shell.logOut") }));
    await waitFor(() => expect(screen.getByTestId("where")).toHaveTextContent("/login"));
  });

  it("shows a household switcher for >1 household and remembers the choice per user", async () => {
    backend.signedIn = true;
    backend.households = [home, cabin];
    const first = renderApp("/");
    const select = await screen.findByLabelText(t("shell.household"));
    expect(select).toHaveValue("h1");
    await userEvent.selectOptions(select, "h2");
    expect(select).toHaveValue("h2");
    expect(window.localStorage.getItem(`foodplanner.activeHousehold.${user.userId}`)).toBe("h2");
    first.unmount();

    renderApp("/");
    expect(await screen.findByLabelText(t("shell.household"))).toHaveValue("h2");
  });
});
