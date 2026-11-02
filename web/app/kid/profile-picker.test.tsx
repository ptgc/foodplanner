import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { SessionProvider, useSession } from "../session";
import { t } from "../test-i18n";
import { install, newFake, type Fake } from "./fake-server";
import { ProfilePicker } from "./profile-picker";

const mia = { id: "p-mia", displayName: "Mia", avatar: "fox" };
const ola = { id: "p-ola", displayName: "Ola", avatar: "owl" };

let fake: Fake;

beforeEach(() => {
  fake = newFake();
  fake.profiles = [mia, ola];
  install(fake);
});

afterEach(() => {
  vi.unstubAllGlobals();
});

function Probe() {
  const { session } = useSession();
  if (session.status !== "signedIn") return null;
  return <output data-testid="mode">{session.user.kidMode ? `kid:${session.user.kidMode.profileId}` : "adult"}</output>;
}

function renderPicker(kidMode: boolean, onClose = vi.fn()) {
  render(
    <MemoryRouter>
      <SessionProvider>
        <Probe />
        <ProfilePicker householdId="h1" kidMode={kidMode} activeProfileId={null} onClose={onClose} />
      </SessionProvider>
    </MemoryRouter>,
  );
  return onClose;
}

describe("profile picker", () => {
  it("shows one big tile per kid, with the name, and switches with one tap (adult with a PIN)", async () => {
    const u = userEvent.setup();
    const onClose = renderPicker(false);
    const tiles = await screen.findAllByTestId("profile-tile");
    expect(tiles.map((b) => b.textContent)).toEqual([expect.stringContaining("Mia"), expect.stringContaining("Ola")]);
    await screen.findByText("adult");
    await u.click(within(screen.getByTestId("profile-picker")).getByRole("button", { name: t("kid.picker.switchTo", { name: "Ola" }) }));
    await waitFor(() => expect(screen.getByTestId("mode")).toHaveTextContent("kid:p-ola"));
    expect(onClose).toHaveBeenCalled();
  });

  it("an adult can reach 'new profile'; kid mode has no such link", async () => {
    renderPicker(false);
    expect(await screen.findByRole("link", { name: t("kid.picker.newProfile") })).toHaveAttribute("href", "/household");
  });

  it("kid mode hides the new-profile link", async () => {
    renderPicker(true);
    await screen.findAllByTestId("profile-tile");
    expect(screen.queryByRole("link", { name: t("kid.picker.newProfile") })).not.toBeInTheDocument();
  });

  it("asks an adult without a PIN to choose one, then carries on with the tapped profile", async () => {
    const u = userEvent.setup();
    fake.pinSet = false;
    renderPicker(false);
    await u.click(await screen.findByRole("button", { name: t("kid.picker.switchTo", { name: "Mia" }) }));
    const needs = await screen.findByTestId("picker-needs-pin");
    expect(needs).toHaveTextContent(t("kid.picker.needsPin"));
    await u.type(within(needs).getByLabelText(t("kid.pinSetup.new")), "4827");
    await u.click(within(needs).getByRole("button", { name: t("kid.pinSetup.save") }));
    await waitFor(() => expect(screen.getByTestId("mode")).toHaveTextContent("kid:p-mia"));
    expect(fake.calls).toContain("PUT /api/auth/pin");
  });

  it("says so when there are no kid profiles yet", async () => {
    fake.profiles = [];
    renderPicker(false);
    expect(await screen.findByTestId("picker-empty")).toBeInTheDocument();
  });
});
