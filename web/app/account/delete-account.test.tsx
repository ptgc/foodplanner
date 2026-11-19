import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes } from "react-router";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { components } from "../api/schema";
import { json, problem } from "../kid/fake-server";
import Household from "../routes/household";
import Login from "../routes/login";
import AccountDeleteRoute from "../routes/account-delete";
import Shell from "../routes/shell";
import { SessionProvider } from "../session";
import { t } from "../test-i18n";

type Preview = components["schemas"]["AccountDeletionPreviewResponse"];

const email = "Anne.Berit@example.test";
const solo = { id: "h-solo", name: "Solo kitchen", memberCount: 1 };
const shared = { id: "h-shared", name: "Family", memberCount: 3 };
const blocked = { id: "h-blocked", name: "Cabin", memberCount: 2 };

const deletable: Preview = { canDelete: true, confirmationText: email, householdsToDelete: [solo], blockingHouseholds: [], householdsKept: [shared] };
const blocking: Preview = { canDelete: false, confirmationText: email, householdsToDelete: [], blockingHouseholds: [blocked], householdsKept: [shared] };

type Stub = {
  preview: Preview;
  kidMode: boolean;
  /** What POST /api/account/delete answers; a function can throw (offline). */
  deleteAnswer: () => Response;
  deleteBodies: unknown[];
  calls: string[];
};

let stub: Stub;

beforeEach(() => {
  stub = { preview: deletable, kidMode: false, deleteAnswer: () => new Response(null, { status: 204 }), deleteBodies: [], calls: [] };
  vi.stubGlobal(
    "fetch",
    vi.fn(async (request: Request) => {
      const path = new URL(request.url).pathname;
      stub.calls.push(`${request.method} ${path}`);
      if (path === "/api/auth/me") {
        return json({ userId: "u1", email, kidMode: stub.kidMode ? { profileId: null, householdId: "h-shared" } : null });
      }
      if (path === "/api/households") {
        return json([
          { id: solo.id, name: solo.name, role: "Owner" },
          { id: shared.id, name: shared.name, role: "Adult" },
          { id: blocked.id, name: blocked.name, role: "Owner" },
        ]);
      }
      if (path === "/api/auth/antiforgery") return json({ token: "t" });
      if (path === "/api/account/deletion-preview") return json(stub.preview);
      if (path === "/api/account/delete") {
        stub.deleteBodies.push(await request.json());
        return stub.deleteAnswer();
      }
      throw new Error(`unexpected ${request.method} ${path}`);
    }),
  );
  window.localStorage.clear();
});

afterEach(() => {
  vi.unstubAllGlobals();
});

function renderApp(url = "/account/delete") {
  return render(
    <MemoryRouter initialEntries={[url]}>
      <SessionProvider>
        <Routes>
          <Route path="login" element={<Login />} />
          <Route element={<Shell />}>
            <Route index element={<p>home page</p>} />
            <Route path="household" element={<Household />} />
            <Route path="account/delete" element={<AccountDeleteRoute />} />
          </Route>
        </Routes>
      </SessionProvider>
    </MemoryRouter>,
  );
}

async function fill(u: ReturnType<typeof userEvent.setup>, password = "secret", typed = email) {
  await u.type(await screen.findByLabelText(t("account.delete.password")), password);
  await u.type(screen.getByLabelText(t("account.delete.confirmation", { email })), typed);
}

const submit = () => screen.getByTestId("delete-submit");
const error = () => screen.findByTestId("delete-error");

describe("Delete account screen (T-095)", () => {
  it("lists what is deleted, what is kept, and the export links; the button starts disabled", async () => {
    renderApp();
    const toDelete = await screen.findByTestId("households-to-delete");
    expect(within(toDelete).getByText(solo.name)).toBeInTheDocument();
    expect(within(screen.getByTestId("households-kept")).getByText(shared.name)).toBeInTheDocument();
    expect(screen.queryByTestId("blocking-households")).not.toBeInTheDocument();
    expect(screen.getByTestId("kept-household")).toHaveTextContent(t("account.delete.members", { count: 3 }));
    expect(screen.getByText(t("account.delete.kept.body"))).toBeInTheDocument();
    // Owner of the solo household and Adult in the shared one may export; both are linked.
    const links = within(screen.getByTestId("export-links")).getAllByRole("link");
    expect(links.map((l) => l.textContent)).toEqual([t("account.delete.export.link", { name: solo.name }), t("account.delete.export.link", { name: shared.name })]);
    expect(submit()).toBeDisabled();
  });

  it("blocking households show 'transfer ownership first' with a link to the members and no form", async () => {
    stub.preview = blocking;
    const u = userEvent.setup();
    renderApp();
    const block = await screen.findByTestId("blocking-households");
    expect(within(block).getByText(t("account.delete.blocking.title"))).toBeInTheDocument();
    expect(screen.queryByTestId("delete-form")).not.toBeInTheDocument();
    expect(screen.queryByLabelText(t("account.delete.password"))).not.toBeInTheDocument();
    await u.click(within(block).getByRole("link", { name: t("account.delete.blocking.link", { name: blocked.name }) }));
    expect(await screen.findByTestId("household-settings-name")).toHaveTextContent(blocked.name); // the household page works on the blocked one
  });

  it("enables the button only when the password is filled and the typed email matches (case and spaces ignored)", async () => {
    const u = userEvent.setup();
    renderApp();
    await u.type(await screen.findByLabelText(t("account.delete.password")), "secret");
    expect(submit()).toBeDisabled();
    const typed = screen.getByLabelText(t("account.delete.confirmation", { email }));
    await u.type(typed, "anne.berit@example.tes");
    expect(submit()).toBeDisabled();
    await u.type(typed, "t");
    expect(submit()).toBeEnabled();
    await u.clear(typed);
    await u.type(typed, "  ANNE.BERIT@EXAMPLE.TEST ");
    expect(submit()).toBeEnabled();
    await u.clear(screen.getByLabelText(t("account.delete.password")));
    expect(submit()).toBeDisabled();
  });

  it("sends the shown household ids, clears local state and goes to the sign-in page with a message", async () => {
    const u = userEvent.setup();
    window.localStorage.setItem("foodplanner.activeHousehold.u1", shared.id);
    renderApp();
    await fill(u, "secret", "  anne.berit@example.test ");
    await u.click(submit());
    expect(await screen.findByTestId("account-deleted-note")).toHaveTextContent(t("login.accountDeleted"));
    expect(stub.deleteBodies).toEqual([{ password: "secret", confirmation: "  anne.berit@example.test ", householdIds: [solo.id] }]);
    expect(window.localStorage.getItem("foodplanner.activeHousehold.u1")).toBeNull();
    expect(window.localStorage.getItem("foodplanner.session")).toBeNull();
    expect(stub.calls).not.toContain("POST /api/auth/logout"); // the account is gone; no logout call
  });

  it("a wrong password shows the password message, empties the field and keeps the form", async () => {
    stub.deleteAnswer = () => problem(403, "password-incorrect");
    const u = userEvent.setup();
    renderApp();
    await fill(u);
    await u.click(submit());
    expect(await error()).toHaveTextContent(t("account.delete.problem.passwordIncorrect"));
    expect(screen.getByLabelText(t("account.delete.password"))).toHaveValue("");
    expect(screen.queryByTestId("account-deleted-note")).not.toBeInTheDocument();
  });

  it("account-owns-households refreshes the preview and says to transfer ownership", async () => {
    stub.deleteAnswer = () => {
      stub.preview = blocking;
      return problem(409, "account-owns-households", { households: [blocked] });
    };
    const u = userEvent.setup();
    renderApp();
    await fill(u);
    await u.click(submit());
    expect(await error()).toHaveTextContent(t("account.delete.problem.ownsHouseholds"));
    expect(await screen.findByTestId("blocking-households")).toBeInTheDocument();
    expect(screen.queryByTestId("delete-form")).not.toBeInTheDocument();
    expect(stub.calls.filter((c) => c === "GET /api/account/deletion-preview")).toHaveLength(2);
  });

  it("account-deletion-changed refreshes the preview and says it changed", async () => {
    stub.deleteAnswer = () => {
      stub.preview = { ...deletable, householdsToDelete: [solo, { id: "h-new", name: "New place", memberCount: 1 }] };
      return problem(409, "account-deletion-changed", { households: stub.preview.householdsToDelete });
    };
    const u = userEvent.setup();
    renderApp();
    await fill(u);
    await u.click(submit());
    expect(await error()).toHaveTextContent(t("account.delete.changed"));
    expect(await screen.findByText("New place")).toBeInTheDocument();
    // The next submit sends the ids that are shown now.
    stub.deleteAnswer = () => new Response(null, { status: 204 });
    await u.click(submit());
    await screen.findByTestId("account-deleted-note");
    expect(stub.deleteBodies[1]).toMatchObject({ householdIds: [solo.id, "h-new"] });
  });

  it("429 says how long to wait from Retry-After", async () => {
    stub.deleteAnswer = () => json({ status: 429, type: "urn:foodplanner:problem:rate-limited" }, 429, { "Retry-After": "42" });
    const u = userEvent.setup();
    renderApp();
    await fill(u);
    await u.click(submit());
    expect(await error()).toHaveTextContent(t("account.delete.problem.rateLimited", { seconds: 42 }));
  });

  it("429 without Retry-After asks to wait a minute", async () => {
    stub.deleteAnswer = () => json({ status: 429, type: "urn:foodplanner:problem:rate-limited" }, 429);
    const u = userEvent.setup();
    renderApp();
    await fill(u);
    await u.click(submit());
    expect(await error()).toHaveTextContent(t("account.delete.problem.rateLimitedNoTime"));
  });

  it("offline: says the API cannot be reached and stays on the form", async () => {
    stub.deleteAnswer = () => {
      throw new TypeError("Failed to fetch");
    };
    const u = userEvent.setup();
    renderApp();
    await fill(u);
    await u.click(submit());
    expect(await error()).toHaveTextContent(t("problem.unreachable"));
    expect(submit()).toBeEnabled();
  });

  it("400 validation shows the fill-in message", async () => {
    stub.deleteAnswer = () => problem(400, "validation", { errors: { confirmation: ["x"] } });
    const u = userEvent.setup();
    renderApp();
    await fill(u);
    await u.click(submit());
    expect(await error()).toHaveTextContent(t("account.delete.problem.validation"));
  });

  it("a preview that cannot be read shows an error with a retry", async () => {
    const real = globalThis.fetch;
    let fail = true;
    vi.stubGlobal("fetch", (request: Request) => (fail && new URL(request.url).pathname === "/api/account/deletion-preview" ? Promise.reject(new TypeError("offline")) : real(request)));
    const u = userEvent.setup();
    renderApp();
    expect(await screen.findByText(new RegExp(t("account.delete.loadFailed")))).toBeInTheDocument();
    expect(screen.queryByTestId("delete-form")).not.toBeInTheDocument();
    fail = false;
    await u.click(screen.getByRole("button", { name: t("account.delete.retry") }));
    expect(await screen.findByTestId("delete-form")).toBeInTheDocument();
  });
});

describe("Delete account entry and kid mode (T-095)", () => {
  it("the household page offers the entry, and it leads to the screen", async () => {
    const u = userEvent.setup();
    renderApp("/household");
    await u.click(await screen.findByTestId("delete-account-link"));
    expect(await screen.findByRole("heading", { name: t("account.delete.title") })).toBeInTheDocument();
  });

  it("in kid mode the shell shows the kid app: no entry, and the screen route never renders the form", async () => {
    stub.kidMode = true;
    renderApp("/account/delete");
    await waitFor(() => expect(screen.queryByText(t("common.loading"))).not.toBeInTheDocument());
    expect(screen.queryByTestId("delete-account-link")).not.toBeInTheDocument();
    expect(screen.queryByTestId("delete-form")).not.toBeInTheDocument();
    expect(stub.calls).not.toContain("GET /api/account/deletion-preview");
  });
});
