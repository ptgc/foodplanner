import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { json } from "../kid/fake-server";
import { SessionProvider, useSession } from "../session";

// T-095 verification: after the account is deleted nothing household-owned stays in this browser.
function Probe() {
  const { session, accountDeleted } = useSession();
  if (session.status !== "signedIn") return <p>{session.status}</p>;
  return <button onClick={() => void accountDeleted()}>go</button>;
}

describe("accountDeleted clean-up", () => {
  const deletedCaches: string[] = [];

  beforeEach(() => {
    deletedCaches.length = 0;
    vi.stubGlobal(
      "fetch",
      vi.fn(async (request: Request) => {
        const path = new URL(request.url).pathname;
        if (path === "/api/auth/me") return json({ userId: "u1", email: "a@example.test", kidMode: null });
        if (path === "/api/households") return json([{ id: "h1", name: "Home", role: "Owner" }]);
        throw new Error(`unexpected ${request.method} ${path}`);
      }),
    );
    vi.stubGlobal("caches", {
      keys: async () => ["fp-precache-1", "fp-runtime-recipes", "fp-runtime-images"],
      delete: async (name: string) => {
        deletedCaches.push(name);
        return true;
      },
    });
    window.localStorage.clear();
    window.sessionStorage.clear();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("removes the saved session, the household choice, the shopping cache and queue, and the runtime caches", async () => {
    render(
      <SessionProvider>
        <Probe />
      </SessionProvider>,
    );
    const button = await screen.findByText("go");
    window.localStorage.setItem("foodplanner.activeHousehold.u1", "h1");
    window.localStorage.setItem("foodplanner.shopping.list.h1.2031-03-03", "{}");
    window.localStorage.setItem("foodplanner.shopping.queue", "[]");
    expect(window.localStorage.getItem("foodplanner.session")).not.toBeNull();

    await userEvent.setup().click(button);
    await waitFor(() => expect(screen.getByText("signedOut")).toBeInTheDocument());

    const left = Object.keys(window.localStorage).filter((k) => k.startsWith("foodplanner."));
    expect(left).toEqual([]);
    expect(window.sessionStorage.length).toBe(0);
    expect(deletedCaches.sort()).toEqual(["fp-runtime-images", "fp-runtime-recipes"]);
  });
});
