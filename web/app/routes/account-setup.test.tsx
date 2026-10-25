import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes, useLocation } from "react-router";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { resetAntiforgeryToken } from "../api/client";
import { t } from "../test-i18n";
import { SessionProvider } from "../session";
import Login from "./login";
import Register from "./register";
import SetPassword from "./set-password";

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": status >= 400 ? "application/problem+json" : "application/json" },
  });

type Backend = { register: () => Response; confirm: () => Response };
let backend: Backend;
let requests: { path: string; body: unknown }[];

beforeEach(() => {
  resetAntiforgeryToken();
  window.localStorage.clear();
  requests = [];
  backend = { register: () => json({ message: "ok" }, 202), confirm: () => new Response(null, { status: 204 }) };
  vi.stubGlobal(
    "fetch",
    vi.fn(async (request: Request) => {
      const path = new URL(request.url).pathname;
      if (request.method === "POST") requests.push({ path, body: await request.clone().json() });
      switch (path) {
        case "/api/auth/antiforgery":
          return json({ token: "t" });
        case "/api/auth/register":
          return backend.register();
        case "/api/auth/confirm-email":
          return backend.confirm();
        default:
          return json({ title: "Unauthenticated", status: 401 }, 401);
      }
    }),
  );
});
afterEach(() => vi.unstubAllGlobals());

function Where() {
  const l = useLocation();
  return <p data-testid="where">{l.pathname + l.search}</p>;
}

function renderAt(initial: string) {
  return render(
    <MemoryRouter initialEntries={[initial]}>
      <SessionProvider>
        <Where />
        <Routes>
          <Route path="login" element={<Login />} />
          <Route path="register" element={<Register />} />
          <Route path="set-password" element={<SetPassword />} />
        </Routes>
      </SessionProvider>
    </MemoryRouter>,
  );
}

describe("register", () => {
  it("sends only the email and shows the same message whatever the server knows", async () => {
    renderAt("/register");
    await userEvent.type(await screen.findByLabelText(t("login.email")), "new@example.test");
    await userEvent.click(screen.getByRole("button", { name: t("register.submit") }));
    expect(await screen.findByRole("status")).toHaveTextContent(t("register.sent"));
    expect(requests).toEqual([{ path: "/api/auth/register", body: { email: "new@example.test" } }]);
  });

  it("shows a rate-limit message on 429", async () => {
    backend.register = () => json({ type: "urn:foodplanner:problem:rate-limited", status: 429 }, 429);
    renderAt("/register");
    await userEvent.type(await screen.findByLabelText(t("login.email")), "a@example.test");
    await userEvent.click(screen.getByRole("button", { name: t("register.submit") }));
    expect(await screen.findByRole("alert")).toHaveTextContent(t("problem.rateLimited"));
  });

  it("is reachable from the login page", async () => {
    renderAt("/login");
    await userEvent.click(await screen.findByRole("link", { name: t("login.createAccount") }));
    expect(await screen.findByRole("heading", { name: t("register.title") })).toBeInTheDocument();
  });
});

describe("set password", () => {
  const link = "/set-password?userId=11111111-1111-1111-1111-111111111111&token=abc";

  async function fill(password: string, repeat = password) {
    await userEvent.type(await screen.findByLabelText(t("setPassword.password")), password);
    await userEvent.type(screen.getByLabelText(t("setPassword.confirm")), repeat);
    await userEvent.click(screen.getByRole("button", { name: t("setPassword.submit") }));
  }

  it("shows the rules, posts the password with the link values and goes to login with a message", async () => {
    renderAt(link);
    expect(await screen.findByText(t("setPassword.rules", { count: 10 }))).toBeInTheDocument();
    await fill("a long enough password");
    await waitFor(() => expect(screen.getByTestId("where")).toHaveTextContent("/login?passwordSet=1"));
    expect(await screen.findByRole("status")).toHaveTextContent(t("login.passwordSet"));
    expect(requests).toEqual([
      {
        path: "/api/auth/confirm-email",
        body: { userId: "11111111-1111-1111-1111-111111111111", token: "abc", password: "a long enough password" },
      },
    ]);
  });

  it("removes the token from the URL after mount but still submits it", async () => {
    renderAt(link);
    await waitFor(() => expect(screen.getByTestId("where")).toHaveTextContent(/^\/set-password$/));
    expect(screen.getByTestId("where").textContent).not.toContain("token");
    await fill("a long enough password");
    await waitFor(() => expect(requests).toHaveLength(1));
    expect(requests[0]?.body).toMatchObject({ token: "abc" });
  });

  it("asks the browser not to send a referrer", async () => {
    renderAt(link);
    await screen.findByLabelText(t("setPassword.password"));
    expect(document.querySelector('meta[name="referrer"][content="no-referrer"]')).not.toBeNull();
  });

  it("does not post when the passwords differ or are too short", async () => {
    renderAt(link);
    await fill("a long enough password", "something different");
    expect(await screen.findByRole("alert")).toHaveTextContent(t("setPassword.mismatch"));
    expect(requests).toEqual([]);
    await userEvent.clear(screen.getByLabelText(t("setPassword.password")));
    await userEvent.type(screen.getByLabelText(t("setPassword.password")), "short");
    await userEvent.click(screen.getByRole("button", { name: t("setPassword.submit") }));
    expect(screen.getByRole("alert")).toHaveTextContent(t("setPassword.tooShort", { count: 10 }));
    expect(requests).toEqual([]);
  });

  it("offers a new link when the link is invalid", async () => {
    backend.confirm = () => json({ type: "urn:foodplanner:problem:confirmation-invalid", status: 400 }, 400);
    renderAt(link);
    await fill("a long enough password");
    expect(await screen.findByRole("alert")).toHaveTextContent(t("setPassword.invalidLink"));
    expect(screen.getByRole("link", { name: t("setPassword.registerAgain") })).toHaveAttribute("href", "/register");
  });

  it("shows a server password rejection without losing the form", async () => {
    backend.confirm = () => json({ title: "One or more validation errors occurred.", status: 400, errors: { password: ["x"] } }, 400);
    renderAt(link);
    await fill("a long enough password");
    expect(await screen.findByRole("alert")).toHaveTextContent(t("setPassword.rejected"));
    expect(screen.getByRole("button", { name: t("setPassword.submit") })).toBeEnabled();
  });

  it("treats a link without a token as invalid", async () => {
    renderAt("/set-password");
    expect(await screen.findByRole("alert")).toHaveTextContent(t("setPassword.invalidLink"));
  });
});
