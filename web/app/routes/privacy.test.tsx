import { render, screen, within } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router";
import { afterEach, describe, expect, it, vi } from "vitest";
import { en } from "../i18n/en";
import { nb } from "../i18n/nb";
import { privacyEn } from "../i18n/privacy-en";
import { privacyNb } from "../i18n/privacy-nb";
import { SessionProvider } from "../session";
import { t } from "../test-i18n";
import Login from "./login";
import Privacy from "./privacy";

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "content-type": status >= 400 ? "application/problem+json" : "application/json" } });

function stubApi(signedIn: boolean) {
  vi.stubGlobal(
    "fetch",
    vi.fn(async (request: Request) => {
      const path = new URL(request.url).pathname;
      if (path === "/api/auth/me") return signedIn ? json({ userId: "u1", email: "a@example.test", kidMode: null }) : json({ status: 401, title: "x" }, 401);
      if (path === "/api/auth/antiforgery") return json({ token: "t" });
      if (path === "/api/households") return json([{ id: "h1", name: "Home", role: "Owner" }]);
      throw new Error(`unexpected ${path}`);
    }),
  );
}

function renderAt(path: string) {
  render(
    <MemoryRouter initialEntries={[path]}>
      <SessionProvider>
        <Routes>
          <Route path="/privacy" element={<Privacy />} />
          <Route path="/login" element={<Login />} />
        </Routes>
      </SessionProvider>
    </MemoryRouter>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  localStorage.clear();
});

describe("privacy page", () => {
  it("says what is stored, the cookies, the Anthropic line, Oda and the user's choices", async () => {
    stubApi(true);
    renderAt("/privacy");
    expect(await screen.findByRole("heading", { level: 1, name: t("privacy.title") })).toBeInTheDocument();
    for (const key of ["privacy.stores.title", "privacy.cookies.title", "privacy.ai.title", "privacy.oda.title", "privacy.rights.title"] as const) {
      expect(screen.getByRole("heading", { level: 2, name: t(key) })).toBeInTheDocument();
    }
    expect(screen.getByText(t("privacy.cookies.signin"))).toBeInTheDocument();
    expect(screen.getByText(t("privacy.cookies.oda"))).toBeInTheDocument();
    // The AI line is the one from the import screen, plus the scope; Anthropic is named.
    const ai = screen.getByTestId("privacy-ai");
    expect(within(ai).getByText(t("privacy.ai.body"))).toHaveTextContent("Anthropic");
    expect(ai).toHaveTextContent(t("import.aiPrivacy").replace(/[.]$/, ""));
    expect(screen.getByTestId("privacy-oda")).toHaveTextContent(t("privacy.oda.body"));
  });

  it("links to the household export and account deletion when signed in", async () => {
    stubApi(true);
    renderAt("/privacy");
    expect(await screen.findByTestId("privacy-export-link")).toHaveAttribute("href", "/household");
    expect(screen.getByTestId("privacy-delete-link")).toHaveAttribute("href", "/account/delete");
  });

  it("is readable signed out, without those links", async () => {
    stubApi(false);
    renderAt("/privacy");
    expect(await screen.findByRole("heading", { level: 1, name: t("privacy.title") })).toBeInTheDocument();
    expect(screen.queryByTestId("privacy-export-link")).not.toBeInTheDocument();
    expect(screen.queryByTestId("privacy-delete-link")).not.toBeInTheDocument();
    expect(screen.getByRole("link", { name: t("privacy.back") })).toHaveAttribute("href", "/login");
  });

  it("is linked from the sign-in screen", async () => {
    stubApi(false);
    renderAt("/login");
    expect(await screen.findByTestId("privacy-link")).toHaveAttribute("href", "/privacy");
  });

  it("has the same keys in nb and en", () => {
    expect(Object.keys(privacyNb).sort()).toEqual(Object.keys(privacyEn).sort());
    expect(en["privacy.link"]).toBeTruthy();
    expect(nb["privacy.link"]).toBeTruthy();
  });
});
