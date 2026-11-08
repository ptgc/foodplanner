import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes } from "react-router";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { install, newFake, pickedEntry, recipe, type Fake } from "../kid/fake-server";
import { SessionProvider } from "../session";
import { t } from "../test-i18n";
import Plan from "./plan";

const week = "2031-03-03";
const mia = { id: "p-mia", displayName: "Mia", avatar: "fox" };
let fake: Fake;

beforeEach(() => {
  fake = newFake();
  fake.recipes = [recipe("r1", "Fiskesuppe")];
  install(fake);
});

afterEach(() => {
  vi.unstubAllGlobals();
});

function renderPlan() {
  render(
    <MemoryRouter initialEntries={[`/plan?week=${week}`]}>
      <SessionProvider>
        <Routes>
          <Route path="/plan" element={<Plan />} />
        </Routes>
      </SessionProvider>
    </MemoryRouter>,
  );
}

describe("Week lock on the adult planner (T-091)", () => {
  it("an Owner locks the week and unlocks it again; the padlock note follows the server's answer", async () => {
    const u = userEvent.setup();
    renderPlan();
    const toggle = await screen.findByTestId("planner-lock-toggle");
    expect(toggle).toHaveTextContent(t("planner.lock"));
    expect(toggle).toHaveAttribute("aria-pressed", "false");
    expect(screen.getByTestId("planner-lock")).toHaveAttribute("data-locked", "false");

    await u.click(toggle);
    await waitFor(() => expect(screen.getByTestId("planner-lock")).toHaveAttribute("data-locked", "true"));
    expect(fake.calls).toContain(`PUT /api/households/h1/meal-plans/${week}/lock`);
    expect(screen.getByTestId("planner-lock")).toHaveTextContent(t("planner.lockedNote"));
    expect(screen.getByTestId("planner-lock-toggle")).toHaveTextContent(t("planner.unlock"));
    expect(screen.getByTestId("planner-lock-toggle")).toHaveAttribute("aria-pressed", "true");

    await u.click(screen.getByTestId("planner-lock-toggle"));
    await waitFor(() => expect(screen.getByTestId("planner-lock")).toHaveAttribute("data-locked", "false"));
    expect(fake.calls).toContain(`DELETE /api/households/h1/meal-plans/${week}/lock`);
  });

  it("a locked week stays editable for an adult (the add buttons still work)", async () => {
    fake.locked = true;
    renderPlan();
    await screen.findByTestId("planner-lock-toggle");
    expect(screen.getByTestId("planner-lock")).toHaveAttribute("data-locked", "true");
    for (const plus of document.querySelectorAll<HTMLButtonElement>(".planner-add")) expect(plus).toBeEnabled();
  });

  it("a Child member sees the padlock note but no toggle", async () => {
    fake.role = "Child";
    fake.locked = true;
    renderPlan();
    expect(await screen.findByText(t("planner.lockedNote"))).toBeInTheDocument();
    expect(screen.queryByTestId("planner-lock-toggle")).not.toBeInTheDocument();
  });

  it("shows a kid's pick with the kid's avatar and name", async () => {
    fake.entries = [pickedEntry("e1", week, fake.recipes[0]!, { profile: mia })];
    renderPlan();
    const avatar = await screen.findByRole("img", { name: t("planner.pickedBy", { who: "Mia" }) });
    expect(avatar).toHaveAttribute("data-avatar", "fox");
  });

  it("shows a pick whose account was deleted with a neutral avatar and the label \"Deleted user\" (T-095)", async () => {
    const entry = pickedEntry("e1", week, fake.recipes[0]!);
    fake.entries = [{ ...entry, pickedBy: { userId: null, email: null } }];
    renderPlan();
    const avatar = await screen.findByRole("img", { name: t("planner.pickedBy", { who: t("common.deletedUser") }) });
    expect(avatar).toHaveClass("avatar-deleted");
    expect(avatar.style.backgroundColor).toBe(""); // no personal colour
    expect(avatar).toHaveTextContent("-");
  });
});
