import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes } from "react-router";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { install, newFake, recipe, type Fake } from "../kid/fake-server";
import { SessionProvider } from "../session";
import { t } from "../test-i18n";
import Home from "./home";
import Household from "./household";
import Plan from "./plan";
import Shell from "./shell";

const mia = { id: "p-mia", displayName: "Mia", avatar: "fox" };
let fake: Fake;

beforeEach(() => {
  fake = newFake();
  fake.profiles = [mia];
  fake.recipes = [recipe("r1", "Pannekaker")];
  install(fake);
});

afterEach(() => {
  vi.unstubAllGlobals();
});

function renderApp(url = "/") {
  return render(
    <MemoryRouter initialEntries={[url]}>
      <SessionProvider>
        <Routes>
          <Route element={<Shell />}>
            <Route index element={<Home />} />
            <Route path="plan" element={<Plan />} />
            <Route path="household" element={<Household />} />
            <Route path="shopping" element={<p>shopping page</p>} />
          </Route>
        </Routes>
      </SessionProvider>
    </MemoryRouter>,
  );
}

describe("Shell: kid mode follows /api/auth/me", () => {
  it("a grown-up session shows the normal app with a Switch profile button", async () => {
    renderApp();
    expect(await screen.findByTestId("user-email")).toBeInTheDocument();
    expect(screen.getByRole("navigation", { name: t("shell.mainNavigation") })).toBeInTheDocument();
    expect(screen.getByTestId("switch-profile")).toBeInTheDocument();
    expect(screen.queryByTestId("kid-shell")).not.toBeInTheDocument();
  });

  it("a Child member has no Switch profile button (no PIN to come back with)", async () => {
    fake.role = "Child";
    renderApp();
    await screen.findByTestId("user-email");
    expect(screen.queryByTestId("switch-profile")).not.toBeInTheDocument();
  });

  it("switching to a kid replaces the whole shell: no email, no menu, only the kid screens", async () => {
    const u = userEvent.setup();
    renderApp("/household");
    await u.click(await screen.findByTestId("switch-profile"));
    await u.click(await screen.findByRole("button", { name: t("kid.picker.switchTo", { name: "Mia" }) }));
    expect(await screen.findByTestId("kid-shell")).toBeInTheDocument();
    expect(screen.queryByTestId("user-email")).not.toBeInTheDocument();
    expect(screen.queryByRole("navigation", { name: t("shell.mainNavigation") })).not.toBeInTheDocument();
    expect(await screen.findByTestId("kid-home")).toBeInTheDocument(); // /household was sent back to the dishes
    expect(screen.queryByText(t("household.title"))).not.toBeInTheDocument();
  });

  it("a device that loads in kid mode shows the kid shell at once", async () => {
    fake.kidMode = { profileId: mia.id, householdId: "h1" };
    renderApp();
    expect(await screen.findByTestId("kid-shell")).toBeInTheDocument();
    expect(screen.queryByTestId("switch-profile")).not.toBeInTheDocument();
  });

  it("the PIN brings the normal app back, and a wrong PIN does not", async () => {
    const u = userEvent.setup();
    fake.kidMode = { profileId: mia.id, householdId: "h1" };
    renderApp();
    await u.click(await screen.findByTestId("kid-unlock"));
    const keypad = await screen.findByRole("group", { name: t("kid.pin.keypad") });
    for (const d of "4821") await u.click(within(keypad).getByRole("button", { name: d }));
    expect(await screen.findByTestId("pin-pips")).toBeInTheDocument();
    expect(screen.getByTestId("kid-shell")).toBeInTheDocument();

    for (const d of "4827") await u.click(within(keypad).getByRole("button", { name: d }));
    expect(await screen.findByTestId("user-email")).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByTestId("kid-shell")).not.toBeInTheDocument());
    expect(screen.getByRole("navigation", { name: t("shell.mainNavigation") })).toBeInTheDocument();
  });

  it("reaches the shopping list from the kid shell by its icon", async () => {
    const u = userEvent.setup();
    fake.kidMode = { profileId: mia.id, householdId: "h1" };
    renderApp();
    await u.click(await screen.findByRole("link", { name: t("kid.nav.shopping") }));
    expect(await screen.findByText("shopping page")).toBeInTheDocument();
    expect(screen.getByTestId("kid-shell")).toBeInTheDocument();
  });
});
