import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes } from "react-router";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { SessionProvider } from "../session";
import { addDays, mondayOf } from "../shopping/week";
import { t } from "../test-i18n";
import { barnevennlig, install, newFake, pickedEntry, problem, recipe, type Fake } from "./fake-server";
import KidApp from "./kid-app";

const mia = { id: "p-mia", displayName: "Mia", avatar: "fox" };
const ola = { id: "p-ola", displayName: "Ola", avatar: "owl" };

const meatballs = recipe("r-meat", "Kjottkaker");
const salmon = recipe("r-fish", "Laks");
const veggie = recipe("r-veg", "Gronnsuppe");
const kidDish = recipe("r-kid", "Pannekaker", [barnevennlig]);

const monday = mondayOf(new Date());
const date = (i: number) => addDays(monday, i);

let fake: Fake;

beforeEach(() => {
  fake = newFake();
  fake.kidMode = { profileId: mia.id, householdId: "h1" };
  fake.profiles = [mia, ola];
  fake.recipes = [meatballs, salmon, veggie, kidDish];
  install(fake);
});

afterEach(() => {
  vi.unstubAllGlobals();
});

function renderKid(url = "/") {
  return render(
    <MemoryRouter initialEntries={[url]}>
      <SessionProvider>
        <Routes>
          <Route element={<KidApp />}>
            <Route index element={null} />
            <Route path="plan" element={null} />
            <Route path="shopping" element={<p>shopping page</p>} />
          </Route>
          <Route path="*" element={<KidApp />} />
        </Routes>
      </SessionProvider>
    </MemoryRouter>,
  );
}

describe("kid shell", () => {
  it("has only the avatar (switch profile) and the key (back to the grown-up) in the header", async () => {
    renderKid();
    const header = await screen.findByRole("banner");
    await waitFor(() => expect(within(header).getByTestId("kid-switch")).toHaveAccessibleName(t("kid.switchProfileNamed", { name: "Mia" })));
    expect(within(header).getAllByRole("button")).toHaveLength(2);
    expect(within(header).getByRole("button", { name: t("kid.unlock") })).toBeInTheDocument();
    expect(screen.queryByText(/@/)).not.toBeInTheDocument(); // no email, no settings
  });

  it("opens the profile picker from the avatar and the PIN sheet from the key", async () => {
    const u = userEvent.setup();
    renderKid();
    await u.click(await screen.findByTestId("kid-switch"));
    expect(await screen.findByRole("dialog", { name: t("kid.picker.title") })).toBeInTheDocument();
    await u.click(screen.getByRole("button", { name: t("kid.close") }));
    await u.click(screen.getByTestId("kid-unlock"));
    expect(await screen.findByRole("dialog", { name: t("kid.pin.title") })).toBeInTheDocument();
  });

  it("asks who you are at once when kid mode has no active profile", async () => {
    fake.kidMode = { profileId: null, householdId: "h1" };
    renderKid();
    expect(await screen.findByRole("dialog", { name: t("kid.picker.title") })).toBeInTheDocument();
    expect(await screen.findAllByTestId("profile-tile")).toHaveLength(2);
  });

  it("sends addresses of the grown-up app back to the dishes", async () => {
    renderKid("/household");
    expect(await screen.findByTestId("kid-home")).toBeInTheDocument();
  });

  it("switches kid to kid with one tap and no PIN", async () => {
    const u = userEvent.setup();
    renderKid();
    await u.click(await screen.findByTestId("kid-switch"));
    await u.click(await screen.findByRole("button", { name: t("kid.picker.switchTo", { name: "Ola" }) }));
    await waitFor(() => expect(fake.kidMode?.profileId).toBe(ola.id));
    await waitFor(() => expect(screen.getByTestId("kid-switch")).toHaveAccessibleName(t("kid.switchProfileNamed", { name: "Ola" })));
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });
});

describe("kid home (feed)", () => {
  it("lists every dish with Barnevennlig first and shows no time, tags, servings or steps", async () => {
    renderKid();
    const dishes = await screen.findAllByTestId("kid-dish");
    expect(dishes.map((d) => d.getAttribute("aria-label"))).toEqual(["Pannekaker", "Kjottkaker", "Laks", "Gronnsuppe"]);
    const text = screen.getByTestId("kid-home").textContent ?? "";
    expect(text).not.toMatch(/min|Barnevennlig|porsjon|serving/i);
    expect(screen.queryByText(t("recipes.minutes", { minutes: 30 }))).not.toBeInTheDocument();
  });

  it("a tap on the heart likes at once, a second tap unlikes, and the state is what the server holds after a reload", async () => {
    const u = userEvent.setup();
    const view = renderKid();
    const heart = (await screen.findAllByTestId("kid-heart"))[1]!; // Kjottkaker
    expect(heart).toHaveAttribute("aria-pressed", "false");
    await u.click(heart);
    expect(heart).toHaveAttribute("aria-pressed", "true");
    await waitFor(() => expect(fake.liked.has("r-meat")).toBe(true));
    expect(fake.calls).toContain("PUT /api/households/h1/recipes/r-meat/like");

    view.unmount();
    renderKid();
    const again = (await screen.findAllByTestId("kid-heart"))[1]!;
    expect(again).toHaveAttribute("aria-pressed", "true");
    await u.click(again);
    expect(again).toHaveAttribute("aria-pressed", "false");
    await waitFor(() => expect(fake.liked.has("r-meat")).toBe(false));
    expect(fake.calls).toContain("DELETE /api/households/h1/recipes/r-meat/like");
  });

  it("gives back the heart (with a shake, no error text) when the server refuses the like", async () => {
    const u = userEvent.setup();
    fake.likeError = problem(409, "no-active-profile");
    renderKid();
    const heart = (await screen.findAllByTestId("kid-heart"))[0]!;
    await u.click(heart);
    await waitFor(() => expect(heart).toHaveAttribute("aria-pressed", "false"));
    expect(heart.querySelector(".kid-shake")).not.toBeNull();
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });
});

describe("kid week", () => {
  it("shows seven big day tiles with the picks of the week", async () => {
    fake.entries = [pickedEntry("e1", date(1), meatballs, { profile: mia })];
    renderKid("/plan");
    const tiles = await screen.findAllByTestId("kid-day");
    expect(tiles).toHaveLength(7);
    expect(tiles[1]).toHaveAttribute("data-filled", "true");
    expect(tiles[0]).toHaveAttribute("data-filled", "false");
  });

  it("offers only the allowed dishes for an empty day (the others are not shown) and adds the chosen one", async () => {
    const u = userEvent.setup();
    fake.allowed[date(6)] = [salmon.id, veggie.id];
    renderKid("/plan");
    const tiles = await screen.findAllByTestId("kid-day");
    await u.click(tiles[6]!);
    const grid = await screen.findByTestId("kid-pick-grid");
    const names = within(grid).getAllByTestId("kid-pick").map((b) => b.getAttribute("aria-label"));
    expect(names.sort()).toEqual([t("kid.week.pick", { title: "Gronnsuppe" }), t("kid.week.pick", { title: "Laks" })].sort());
    expect(within(grid).queryByRole("button", { name: t("kid.week.pick", { title: "Kjottkaker" }) })).not.toBeInTheDocument();

    await u.click(within(grid).getByRole("button", { name: t("kid.week.pick", { title: "Laks" }) }));
    await waitFor(() => expect(fake.entries).toHaveLength(1));
    expect(fake.entries[0]).toMatchObject({ date: date(6), recipeId: "r-fish" });
    await waitFor(() => expect(screen.queryByTestId("kid-pick-sheet")).not.toBeInTheDocument());
    await waitFor(() => expect(screen.getAllByTestId("kid-day")[6]).toHaveAttribute("data-filled", "true"));
  });

  it("keeps the sheet open, shakes the dish and reads the options again when the server refuses the pick", async () => {
    const u = userEvent.setup();
    fake.postError = problem(409, "pick-not-allowed");
    renderKid("/plan");
    await u.click((await screen.findAllByTestId("kid-day"))[3]!);
    const grid = await screen.findByTestId("kid-pick-grid");
    await u.click(within(grid).getByRole("button", { name: t("kid.week.pick", { title: "Laks" }) }));
    await waitFor(() => expect(document.querySelector(".kid-pick.kid-shake")).not.toBeNull());
    expect(fake.entries).toHaveLength(0);
    expect(screen.getByTestId("kid-pick-sheet")).toBeInTheDocument();
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });

  it("says when nothing fits the day", async () => {
    const u = userEvent.setup();
    fake.allowed[date(2)] = [];
    renderKid("/plan");
    await u.click((await screen.findAllByTestId("kid-day"))[2]!);
    expect(await screen.findByTestId("kid-nothing")).toBeInTheDocument();
  });

  it("a kid can change and remove their own pick but only look at a grown-up's", async () => {
    const u = userEvent.setup();
    fake.entries = [
      pickedEntry("own", date(0), salmon, { profile: mia }),
      pickedEntry("adult", date(1), meatballs),
      pickedEntry("sibling", date(2), veggie, { profile: ola }),
    ];
    renderKid("/plan");
    const tiles = await screen.findAllByTestId("kid-day");

    await u.click(tiles[1]!);
    expect(within(await screen.findByRole("dialog")).queryByTestId("kid-remove")).not.toBeInTheDocument();
    await u.click(screen.getByRole("button", { name: t("kid.close") }));
    await u.click(tiles[2]!);
    expect(within(await screen.findByRole("dialog")).queryByTestId("kid-remove")).not.toBeInTheDocument();
    await u.click(screen.getByRole("button", { name: t("kid.close") }));

    await u.click(tiles[0]!);
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByTestId("kid-swap")).toBeInTheDocument();
    await u.click(within(dialog).getByTestId("kid-remove"));
    await waitFor(() => expect(fake.entries.map((e) => e.id)).toEqual(["adult", "sibling"]));
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    await waitFor(() => expect(screen.getAllByTestId("kid-day")[0]).toHaveAttribute("data-filled", "false"));
  });

  it("swapping removes the own pick and opens the dishes for that day", async () => {
    const u = userEvent.setup();
    fake.entries = [pickedEntry("own", date(4), salmon, { profile: mia })];
    renderKid("/plan");
    await u.click((await screen.findAllByTestId("kid-day"))[4]!);
    await u.click(await screen.findByTestId("kid-swap"));
    expect(await screen.findByTestId("kid-pick-sheet")).toBeInTheDocument();
    expect(fake.entries).toHaveLength(0);
  });

  it("a locked week shows a large padlock, no add buttons, and a tap on an empty day only pulses", async () => {
    const u = userEvent.setup();
    fake.locked = true;
    fake.entries = [pickedEntry("own", date(0), salmon, { profile: mia })];
    renderKid("/plan");
    const tiles = await screen.findAllByTestId("kid-day");
    const lock = screen.getByTestId("kid-week-lock");
    expect(lock).toHaveAttribute("data-locked", "true");
    expect(lock).toHaveAccessibleName(t("kid.week.locked"));
    expect(tiles[3]).toHaveAttribute("aria-disabled", "true");
    expect(within(tiles[3]!).queryByTestId("kid-day-add")?.className).toContain("kid-day-add-off");

    await u.click(tiles[3]!);
    expect(screen.queryByTestId("kid-pick-sheet")).not.toBeInTheDocument();
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
    await waitFor(() => expect(screen.getByTestId("kid-week-lock").className).toContain("kid-shake"));

    // Even the kid's own pick cannot be changed while the week is locked.
    await u.click(tiles[0]!);
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).queryByTestId("kid-remove")).not.toBeInTheDocument();
    expect(within(dialog).queryByTestId("kid-swap")).not.toBeInTheDocument();
  });

  it("shows no cooking time, tags or servings on the week", async () => {
    fake.entries = [pickedEntry("own", date(0), salmon, { profile: mia })];
    renderKid("/plan");
    await screen.findAllByTestId("kid-day");
    expect(screen.getByTestId("kid-week").textContent).not.toMatch(/min|porsjon|serving|Barnevennlig/i);
  });
});
