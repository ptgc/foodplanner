import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AVATARS } from "../kid/avatars";
import { json, problem } from "../kid/fake-server";
import { t } from "../test-i18n";
import { KidProfiles } from "./kid-profiles";

type Profile = { id: string; displayName: string; avatar: string };
type Server = {
  profiles: Profile[];
  pinSet: boolean;
  pinCalls: { pin?: string; currentPin?: string | null }[];
  writes: { method: string; path: string; body: unknown }[];
  pinAnswer: Response | null;
  createError: Response | null;
};
let server: Server;

beforeEach(() => {
  server = { profiles: [], pinSet: false, pinCalls: [], writes: [], pinAnswer: null, createError: null };
  vi.stubGlobal(
    "fetch",
    vi.fn(async (request: Request) => {
      const url = new URL(request.url);
      const path = url.pathname;
      const method = request.method;
      if (path === "/api/auth/antiforgery") return json({ token: "t" });
      if (path === "/api/households/h1/profiles" && method === "GET") return json(server.profiles);
      if (path === "/api/households/h1/profiles" && method === "POST") {
        const body = (await request.json()) as { displayName: string; avatar: string };
        server.writes.push({ method, path, body });
        if (server.createError) return server.createError;
        const created = { id: `p${server.profiles.length + 1}`, ...body };
        server.profiles.push(created);
        return json(created, 201);
      }
      const one = path.match(/^\/api\/households\/h1\/profiles\/([^/]+)$/);
      if (one && method === "PUT") {
        const body = (await request.json()) as { displayName: string; avatar: string };
        server.writes.push({ method, path, body });
        server.profiles = server.profiles.map((p) => (p.id === one[1] ? { ...p, ...body } : p));
        return json(server.profiles.find((p) => p.id === one[1]));
      }
      if (one && method === "DELETE") {
        server.writes.push({ method, path, body: null });
        server.profiles = server.profiles.filter((p) => p.id !== one[1]);
        return new Response(null, { status: 204 });
      }
      if (path === "/api/auth/pin" && method === "PUT") {
        const body = (await request.json()) as { pin: string; currentPin: string | null };
        server.pinCalls.push(body);
        if (server.pinAnswer) {
          const answer = server.pinAnswer;
          server.pinAnswer = null;
          return answer;
        }
        server.pinSet = true;
        return new Response(null, { status: 204 });
      }
      throw new Error(`unexpected ${method} ${path}`);
    }),
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("Kid profiles (household settings)", () => {
  it("creates a profile with a name and the chosen avatar, and lists it", async () => {
    const u = userEvent.setup();
    render(<KidProfiles householdId="h1" />);
    expect(await screen.findByText(t("kid.profiles.none"))).toBeInTheDocument();
    await u.click(screen.getByTestId("kid-profile-add"));
    const form = screen.getByTestId("kid-profile-form");
    await u.type(within(form).getByLabelText(t("kid.profiles.name")), "  Mia ");
    await u.click(within(form).getByRole("radio", { name: t("kid.avatar.panda") }));
    await u.click(within(form).getByRole("button", { name: t("kid.profiles.create") }));
    await waitFor(() => expect(screen.getAllByTestId("kid-profile-row")).toHaveLength(1));
    expect(server.writes[0]).toEqual({ method: "POST", path: "/api/households/h1/profiles", body: { displayName: "Mia", avatar: "panda" } });
    expect(screen.queryByTestId("kid-profile-form")).not.toBeInTheDocument();
  });

  it("offers every avatar the API knows (12 keys) as a radio with a name", async () => {
    const u = userEvent.setup();
    render(<KidProfiles householdId="h1" />);
    await u.click(await screen.findByTestId("kid-profile-add"));
    expect(screen.getAllByRole("radio")).toHaveLength(AVATARS.length);
    expect(AVATARS.map(([key]) => key)).toEqual([
      "fox", "bear", "cat", "dog", "owl", "frog", "panda", "rabbit", "lion", "penguin", "koala", "whale",
    ]);
  });

  it("asks for a name before calling the API, and shows the translated server refusal for a duplicate", async () => {
    const u = userEvent.setup();
    render(<KidProfiles householdId="h1" />);
    await u.click(await screen.findByTestId("kid-profile-add"));
    const form = screen.getByTestId("kid-profile-form");
    await u.click(within(form).getByRole("button", { name: t("kid.profiles.create") }));
    expect(await screen.findByTestId("kid-profile-error")).toHaveTextContent(t("kid.profiles.nameRequired"));
    expect(server.writes).toHaveLength(0);

    server.createError = problem(409, "profile-exists");
    await u.type(within(form).getByLabelText(t("kid.profiles.name")), "Mia");
    await u.click(within(form).getByRole("button", { name: t("kid.profiles.create") }));
    expect(await screen.findByTestId("kid-profile-error")).toHaveTextContent(t("kid.problem.profileExists"));
  });

  it("renames a profile and deletes one after confirming", async () => {
    const u = userEvent.setup();
    server.profiles = [
      { id: "p1", displayName: "Mia", avatar: "fox" },
      { id: "p2", displayName: "Ola", avatar: "owl" },
    ];
    render(<KidProfiles householdId="h1" />);
    const rows = await screen.findAllByTestId("kid-profile-row");
    await u.click(within(rows[0]!).getByRole("button", { name: new RegExp(t("kid.profiles.edit")) }));
    const name = screen.getByLabelText(t("kid.profiles.name"));
    await u.clear(name);
    await u.type(name, "Mia B");
    await u.click(screen.getByRole("button", { name: t("kid.profiles.save") }));
    await waitFor(() => expect(screen.getAllByTestId("kid-profile-row")[0]).toHaveTextContent("Mia B"));

    await u.click(within(screen.getAllByTestId("kid-profile-row")[1]!).getByRole("button", { name: new RegExp(t("kid.profiles.delete")) }));
    const dialog = await screen.findByRole("dialog");
    expect(dialog).toHaveTextContent(t("kid.profiles.deleteTitle", { name: "Ola" }));
    await u.click(within(dialog).getByRole("button", { name: t("kid.profiles.delete") }));
    await waitFor(() => expect(screen.getAllByTestId("kid-profile-row")).toHaveLength(1));
    expect(server.profiles.map((p) => p.id)).toEqual(["p1"]);
  });

  it("sets a first PIN; the PIN never goes out except in the PUT body", async () => {
    const u = userEvent.setup();
    render(<KidProfiles householdId="h1" />);
    await u.click(await screen.findByTestId("pin-open"));
    const form = screen.getByTestId("pin-setup");
    await u.type(within(form).getByLabelText(t("kid.pinSetup.new")), "4827");
    await u.click(within(form).getByRole("button", { name: t("kid.pinSetup.save") }));
    expect(await screen.findByTestId("pin-saved")).toBeInTheDocument();
    expect(server.pinCalls).toEqual([{ pin: "4827", currentPin: null, password: null }]);
  });

  it("refuses an obvious PIN on the spot, with no request", async () => {
    const u = userEvent.setup();
    render(<KidProfiles householdId="h1" />);
    await u.click(await screen.findByTestId("pin-open"));
    const form = screen.getByTestId("pin-setup");
    await u.type(within(form).getByLabelText(t("kid.pinSetup.new")), "1234");
    await u.click(within(form).getByRole("button", { name: t("kid.pinSetup.save") }));
    expect(await screen.findByTestId("pin-setup-error")).toHaveTextContent(t("kid.pinSetup.weak"));
    expect(server.pinCalls).toHaveLength(0);
  });

  it("changing an existing PIN needs the current one: the server's 400 turns the field into a required one", async () => {
    const u = userEvent.setup();
    server.pinAnswer = json({ status: 400, title: "x", errors: { currentPin: ["required"] } }, 400);
    render(<KidProfiles householdId="h1" />);
    await u.click(await screen.findByTestId("pin-open"));
    const form = screen.getByTestId("pin-setup");
    await u.type(within(form).getByLabelText(t("kid.pinSetup.new")), "4827");
    await u.click(within(form).getByRole("button", { name: t("kid.pinSetup.save") }));
    expect(await screen.findByTestId("pin-setup-error")).toHaveTextContent(t("kid.pinSetup.needsCurrent"));
    await u.type(within(form).getByLabelText(t("kid.pinSetup.current")), "9183");
    await u.click(within(form).getByRole("button", { name: t("kid.pinSetup.save") }));
    expect(await screen.findByTestId("pin-saved")).toBeInTheDocument();
    expect(server.pinCalls[1]).toMatchObject({ pin: "4827", currentPin: "9183" });
  });
});
