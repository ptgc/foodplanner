import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes } from "react-router";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { SessionProvider } from "../session";
import { t } from "../test-i18n";
import More from "./more";
import Shell from "./shell";

const json = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { "content-type": "application/json" } });

let isSystemAdmin = false;

beforeEach(() => {
  isSystemAdmin = false;
  vi.stubGlobal(
    "fetch",
    vi.fn(async (request: Request) => {
      const path = new URL(request.url).pathname;
      if (path === "/api/auth/me") return json({ userId: "u1", email: "mom@example.test", kidMode: null, isSystemAdmin });
      if (path === "/api/households") return json([{ id: "h1", name: "Home", role: "Owner" }]);
      return json({});
    }),
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
});

function renderApp(url: string) {
  return render(
    <MemoryRouter initialEntries={[url]}>
      <SessionProvider>
        <Routes>
          <Route element={<Shell />}>
            <Route index element={<h1>plan page</h1>} />
            <Route path="plan" element={<h1>plan page</h1>} />
            <Route path="recipes" element={<h1>recipes page</h1>} />
            <Route path="pantry" element={<h1>pantry page</h1>} />
            <Route path="more" element={<More />} />
          </Route>
        </Routes>
      </SessionProvider>
    </MemoryRouter>,
  );
}

const tabs = () => within(screen.getByRole("navigation", { name: t("shell.mainNavigation") }));

describe("App frame (T-137)", () => {
  it("has five tabs in order, and marks the active one", async () => {
    renderApp("/recipes");
    await screen.findByTestId("profile-button");
    const links = tabs().getAllByRole("link");
    expect(links.map((l) => l.textContent)).toEqual([t("nav.today"), t("nav.plan"), t("nav.recipes"), t("nav.shopping"), t("nav.more")]);
    expect(links.map((l) => l.getAttribute("aria-current"))).toEqual([null, null, "page", null, null]);
  });

  it("I dag shows the week plan until the start screen exists, and is the marked tab there", async () => {
    const u = userEvent.setup();
    renderApp("/recipes");
    await screen.findByTestId("profile-button");
    await u.click(tabs().getByRole("link", { name: t("nav.today") }));
    expect(await screen.findByRole("heading", { name: "plan page" })).toBeInTheDocument();
    expect(tabs().getByRole("link", { name: t("nav.today") })).toHaveAttribute("aria-current", "page");
  });

  it("marks Mer for the screens that live under it", async () => {
    renderApp("/pantry");
    await screen.findByTestId("profile-button");
    expect(tabs().getByRole("link", { name: t("nav.more") })).toHaveAttribute("aria-current", "page");
  });

  it("keeps the email, household, language and log out out of the top bar; they sit in the profile sheet", async () => {
    const u = userEvent.setup();
    renderApp("/plan");
    const button = await screen.findByTestId("profile-button");
    expect(screen.queryByText("mom@example.test")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: t("shell.logOut") })).not.toBeInTheDocument();

    await u.click(button);
    const sheet = within(await screen.findByRole("dialog"));
    expect(sheet.getByTestId("user-email")).toHaveTextContent("mom@example.test");
    expect(sheet.getByTestId("household-name")).toHaveTextContent("Home");
    expect(sheet.getByLabelText(t("common.language"))).toBeInTheDocument();
    expect(sheet.getByTestId("switch-profile")).toBeInTheDocument();
    expect(sheet.getByRole("button", { name: t("shell.logOut") })).toBeInTheDocument();

    await u.keyboard("{Escape}");
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(button).toHaveFocus();
  });

  it("Mer lists every screen without a tab, and Support only for a system admin", async () => {
    renderApp("/more");
    await screen.findByTestId("profile-button");
    const hrefs = within(screen.getByRole("main")).getAllByRole("link").filter((l) => !tabs().queryAllByRole("link").includes(l)).map((l) => l.getAttribute("href"));
    expect(hrefs).toEqual(["/pantry", "/what-can-i-make", "/household", "/ingredients", "/privacy"]);
  });

  it("Mer adds Support for a system admin", async () => {
    isSystemAdmin = true;
    renderApp("/more");
    expect(await screen.findByRole("link", { name: t("nav.support") })).toHaveAttribute("href", "/support");
  });
});
