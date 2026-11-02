import { act, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { SessionProvider, useSession } from "../session";
import { t } from "../test-i18n";
import { PinSheet } from "./pin-sheet";

const json = (body: unknown, status = 200, headers: Record<string, string> = {}) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": status >= 400 ? "application/problem+json" : "application/json", ...headers },
  });
const problem = (status: number, code: string, extra: Record<string, unknown> = {}, headers: Record<string, string> = {}) =>
  json({ status, title: "x", type: `urn:foodplanner:problem:${code}`, ...extra }, status, headers);

const user = { userId: "11111111-1111-1111-1111-111111111111", email: "anne@example.test" };
const kid = { profileId: "p1", householdId: "h1" };

type Server = { kidMode: typeof kid | null; unlock: Response[]; unlockBodies: unknown[]; reads: number };
let server: Server;

beforeEach(() => {
  server = { kidMode: kid, unlock: [], unlockBodies: [], reads: 0 };
  vi.stubGlobal(
    "fetch",
    vi.fn(async (request: Request) => {
      const path = new URL(request.url).pathname;
      if (path === "/api/auth/me") {
        server.reads++;
        return json({ ...user, kidMode: server.kidMode });
      }
      if (path === "/api/households") return json([{ id: "h1", name: "Home", role: "Child" }]);
      if (path === "/api/auth/antiforgery") return json({ token: "t" });
      if (path === "/api/auth/pin/unlock") {
        server.unlockBodies.push(await request.json());
        const next = server.unlock.shift();
        if (!next) throw new Error("no unlock answer queued");
        if (next.status === 204) server.kidMode = null;
        return next;
      }
      throw new Error(`unexpected ${request.method} ${path}`);
    }),
  );
});

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

function Probe() {
  const { session } = useSession();
  if (session.status !== "signedIn") return null;
  return <output data-testid="mode">{session.user.kidMode ? "kid" : "adult"}</output>;
}

function renderSheet(onClose = vi.fn()) {
  render(
    <MemoryRouter>
      <SessionProvider>
        <Probe />
        <PinSheet onClose={onClose} />
      </SessionProvider>
    </MemoryRouter>,
  );
  return onClose;
}

const keypad = () => screen.getByRole("group", { name: t("kid.pin.keypad") });
const tap = async (u: ReturnType<typeof userEvent.setup>, digits: string) => {
  for (const d of digits) await u.click(within(keypad()).getByRole("button", { name: d }));
};

describe("PIN sheet (kid to adult)", () => {
  it("is a labelled dialog with a keypad of 0-9 and an erase key, all large touch targets in CSS", async () => {
    renderSheet();
    await screen.findByTestId("mode");
    expect(screen.getByRole("dialog", { name: t("kid.pin.title") })).toBeInTheDocument();
    for (const d of "0123456789") expect(within(keypad()).getByRole("button", { name: d })).toBeEnabled();
    expect(within(keypad()).getByRole("button", { name: t("kid.pin.erase") })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: t("kid.close") })).toBeInTheDocument();
  });

  it("shows the digits as dots and erases the last one", async () => {
    const u = userEvent.setup();
    renderSheet();
    await screen.findByTestId("mode");
    await tap(u, "48");
    expect(screen.getByTestId("pin-dots")).toHaveAttribute("aria-label", t("kid.pin.entered", { count: 2, total: 4 }));
    await u.click(within(keypad()).getByRole("button", { name: t("kid.pin.erase") }));
    expect(screen.getByTestId("pin-dots")).toHaveAttribute("aria-label", t("kid.pin.entered", { count: 1, total: 4 }));
  });

  it("sends the PIN on the fourth digit and, when right, reads the session again: the adult app returns", async () => {
    const u = userEvent.setup();
    server.unlock.push(new Response(null, { status: 204 }));
    renderSheet();
    expect(await screen.findByTestId("mode")).toHaveTextContent("kid");
    await tap(u, "4827");
    await waitFor(() => expect(screen.getByTestId("mode")).toHaveTextContent("adult"));
    expect(server.unlockBodies).toEqual([{ pin: "4827" }]);
  });

  it("a wrong PIN shakes the dots, shows the tries left as pips and says it for screen readers, then accepts a new try", async () => {
    const u = userEvent.setup();
    server.unlock.push(problem(403, "pin-incorrect", { attemptsRemaining: 3 }));
    renderSheet();
    await screen.findByTestId("mode");
    await tap(u, "4821");
    const pips = await screen.findByTestId("pin-pips");
    expect(pips).toHaveAttribute("data-remaining", "3");
    expect(pips.querySelectorAll(".kid-pip-on")).toHaveLength(3);
    expect(screen.getByTestId("pin-dots")).toHaveClass("kid-shake");
    expect(screen.getByTestId("pin-status")).toHaveTextContent(t("kid.pin.wrong"));
    expect(screen.getByTestId("pin-status")).toHaveTextContent(t("kid.pin.triesLeft", { count: 3 }));
    expect(screen.getByTestId("mode")).toHaveTextContent("kid");

    server.unlock.push(new Response(null, { status: 204 }));
    await tap(u, "4827");
    await waitFor(() => expect(screen.getByTestId("mode")).toHaveTextContent("adult"));
  });

  it("the lockout shows a padlock and a countdown from Retry-After, disables the keys and offers the password", async () => {
    const u = userEvent.setup();
    server.unlock.push(problem(429, "pin-locked", {}, { "Retry-After": "120" }));
    renderSheet();
    await screen.findByTestId("mode");
    await tap(u, "4821");
    expect(await screen.findByTestId("pin-locked-timer")).toHaveTextContent("2:00");
    expect(screen.getByTestId("pin-status")).toHaveTextContent(t("kid.pin.locked"));
    for (const button of within(keypad()).getAllByRole("button")) expect(button).toBeDisabled();
    expect(screen.getByRole("button", { name: t("kid.pin.passwordSignIn") })).toBeInTheDocument();
    expect(screen.getByRole("dialog")).toHaveAttribute("data-state", "locked");
  });

  it("the lockout counts down and opens the keypad again at zero", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const u = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    server.unlock.push(problem(429, "pin-locked", {}, { "Retry-After": "3" }));
    renderSheet();
    await screen.findByTestId("mode");
    await tap(u, "4821");
    expect(await screen.findByTestId("pin-locked-timer")).toHaveTextContent("0:03");
    await act(async () => {
      await vi.advanceTimersByTimeAsync(3500);
    });
    await waitFor(() => expect(screen.getByRole("dialog")).toHaveAttribute("data-state", "idle"));
    expect(within(keypad()).getByRole("button", { name: "1" })).toBeEnabled();
  });

  it("closes with the close button and with Escape", async () => {
    const u = userEvent.setup();
    const onClose = renderSheet();
    await screen.findByTestId("mode");
    await u.click(screen.getByRole("button", { name: t("kid.close") }));
    expect(onClose).toHaveBeenCalledTimes(1);
    await u.keyboard("{Escape}");
    expect(onClose).toHaveBeenCalledTimes(2);
  });

  it("takes digits from a physical keyboard too", async () => {
    const u = userEvent.setup();
    server.unlock.push(new Response(null, { status: 204 }));
    renderSheet();
    await screen.findByTestId("mode");
    await u.keyboard("4827");
    await waitFor(() => expect(screen.getByTestId("mode")).toHaveTextContent("adult"));
  });
});
