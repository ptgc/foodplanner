import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { resetAntiforgeryToken } from "../api/client";
import { t } from "../test-i18n";
import { navigation } from "./oda";
import { OdaSend } from "./oda-send";

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": status >= 400 ? "application/problem+json" : "application/json" },
  });

let reply: () => Response;
let posts: number;

beforeEach(() => {
  resetAntiforgeryToken();
  window.sessionStorage.clear();
  posts = 0;
  reply = () => json({ authorizeUrl: "https://oda.example/authorize" });
  vi.stubGlobal(
    "fetch",
    vi.fn(async (request: Request) => {
      const path = new URL(request.url).pathname;
      if (path === "/api/auth/antiforgery") return json({ token: "tok" });
      if (path.endsWith("/oda/start")) {
        posts += 1;
        return reply();
      }
      throw new Error(`unexpected ${request.method} ${path}`);
    }),
  );
});
afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

function renderSend(names = ["Melk", "Kaffe"], online = true) {
  return render(<OdaSend householdId="h1" weekStart="2026-10-19" names={names} online={online} />);
}

async function confirm() {
  await userEvent.click(screen.getByRole("button", { name: t("oda.button") }));
  await userEvent.click(await screen.findByRole("button", { name: t("oda.confirm.submit") }));
}

describe("OdaSend", () => {
  it("hides the button and shows the text-export hint when Oda is not configured (404 oda-not-configured)", async () => {
    reply = () => json({ type: "urn:foodplanner:problem:oda-not-configured", status: 404 }, 404);
    renderSend();
    await confirm();
    expect(await screen.findByTestId("oda-unavailable")).toHaveTextContent(t("oda.notConfigured"));
    expect(screen.queryByRole("button", { name: t("oda.button") })).toBeNull();
  });

  it.each([
    ["oda-nothing-to-add", 409, "oda.problem.nothingToAdd"],
    ["oda-too-many-items", 409, "oda.problem.tooMany"],
  ] as const)("shows %s in the dialog and keeps it open", async (type, status, key) => {
    const go = vi.spyOn(navigation, "go").mockImplementation(() => undefined);
    reply = () => json({ type: `urn:foodplanner:problem:${type}`, status }, status);
    renderSend();
    await confirm();
    expect(await screen.findByRole("alert")).toHaveTextContent(t(key));
    expect(go).not.toHaveBeenCalled();
    expect(screen.getByRole("button", { name: t("oda.confirm.submit") })).toBeEnabled();
  });

  it("shows the rate-limit message on 429 and does not navigate", async () => {
    const go = vi.spyOn(navigation, "go").mockImplementation(() => undefined);
    reply = () => json({ type: "urn:foodplanner:problem:rate-limited", status: 429 }, 429);
    renderSend();
    await confirm();
    expect(await screen.findByRole("alert")).toHaveTextContent(t("problem.rateLimited"));
    expect(go).not.toHaveBeenCalled();
  });

  it("does not follow an authorize address that is not http(s)", async () => {
    const go = vi.spyOn(navigation, "go").mockImplementation(() => undefined);
    reply = () => json({ authorizeUrl: "javascript:alert(1)" });
    renderSend();
    await confirm();
    expect(await screen.findByRole("alert")).toHaveTextContent(t("problem.generic"));
    expect(go).not.toHaveBeenCalled();
  });

  it("cancel closes the dialog without calling the API", async () => {
    renderSend();
    await userEvent.click(screen.getByRole("button", { name: t("oda.button") }));
    await userEvent.click(await screen.findByRole("button", { name: t("household.dialog.cancel") }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(posts).toBe(0);
  });

  it("is disabled offline", () => {
    renderSend(["Melk"], false);
    expect(screen.getByRole("button", { name: t("oda.button") })).toBeDisabled();
  });
});
